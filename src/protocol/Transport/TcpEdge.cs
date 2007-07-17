/*
This program is part of BruNet, a library for the creation of efficient overlay
networks.
Copyright (C) 2005  University of California
Copyright (C) 2007 P. Oscar Boykin <boykin@pobox.com> University of Florida

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

//#define POB_TCP_DEBUG

//If you want to copy incoming packets rather than reference the buffer
//define:
#define COPY_PACKETS
using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Brunet
{

  /**
   * A Edge which does its transport over the Tcp protocol.
   * The UDP protocol is really better for Brunet.
   */

  public class TcpEdge : Brunet.Edge
  {

    /**
     * Represents the state of the packet sending
     */
    private class SendState {
      /* Represents the part of the buffer we just tried to send */
      public byte[] Buffer;
      public int Offset;
      public int Length;
    }

    /**
     * Represents the state of the packet receiving 
     */
    private class ReceiveState {
      public byte[] Buffer;
      public int Offset;
      public int Length;
      public int LastReadOffset;
      public int LastReadLength;
      public bool ReadingSize;
      public void Reset(byte[] buf, int off, int length, bool readingsize) {
        Buffer = buf;
        Offset = off;
        Length = length;
        LastReadOffset = off;
        LastReadLength = length;
        ReadingSize = readingsize;
      }
#if COPY_PACKET
      //Make a copy of the internal buffer and drop the reference to the
      //original
      public void CopyBuffer() {
        byte[] tmp_buf = Buffer;
        Buffer = new byte[ length ];
        Array.Copy(tmp_buf, Offset, Buffer, 0, length);
        int tmp_offset = Offset;
        Offset = 0;
        LastReadOffset -= tmp_offset;
      }
#endif
    }

    /**
     * Adding logger
     */
    /*private static readonly log4net.ILog log =
      log4net.LogManager.GetLogger(System.Reflection.MethodBase.
      GetCurrentMethod().DeclaringType);*/

    protected readonly Socket _sock;
    public Socket Socket { get { return _sock; } }
    protected readonly bool inbound;
    protected bool _is_closed;

    protected bool _need_to_send;
    protected readonly TcpEdgeListener _tel;
    public bool NeedToSend {
      get {
        lock( _sync ) {
          return _need_to_send;
        }
      }
      set {
#if POB_TCP_DEBUG
        Console.Error.WriteLine("In NeedToSend");
#endif
        bool send_event = false;
        lock( _sync ) {
          if( _need_to_send != value ) {
            _need_to_send = value;
            send_event = true;
          }
        }
        //Release the lock and fire the event
        if( send_event ) {
#if POB_TCP_DEBUG
          Console.Error.WriteLine("About to send event");
#endif
          _tel.SendStateChange(this);
        }
      }
    }

    /** 
     * Send(Packet) is called faster than we can send
     * the packets over the socket, we place them in
     * queue
     */
    protected readonly Queue _packet_queue;
    /*
     * This keeps track of the number of queued packets
     * We use Interlocked methods for thread safety.  We
     * do this to avoid holding lock on _sync to get the
     * _packet_queue.Count
     */
    protected int _queued_packets;
    /**
     * These objects
     * keep track of the state
     * These are only accessed by the DoSend and DoReceive methods,
     * which are only called in the socket thread of TcpEdgeListener,
     * there is no need to lock before getting access to them.
     */
    private readonly SendState _send_state;
    private readonly ReceiveState _rec_state;
    private const int MAX_QUEUE_SIZE = 30;

    public TcpEdge(Socket s, bool is_in, TcpEdgeListener tel) {
      _sock = s;
      _is_closed = false;
      _create_dt = DateTime.UtcNow;
      _last_out_packet_datetime = _create_dt;
      _last_in_packet_datetime = _last_out_packet_datetime;
      _packet_queue = new Queue(MAX_QUEUE_SIZE);
      _queued_packets = 0;
      _need_to_send = false;
	_tel = tel;
      inbound = is_in;
      _local_ta = TransportAddressFactory.CreateInstance(TAType,
                             (IPEndPoint) _sock.LocalEndPoint);
      _remote_ta = TransportAddressFactory.CreateInstance(TAType,
                             (IPEndPoint) _sock.RemoteEndPoint);
      //We use Non-blocking sockets here
      s.Blocking = false;
      _rec_state = new ReceiveState();
      _send_state = new SendState();
    }

    /**
     * Closes the edges IF it is not already closed, otherwise do nothing
     */
    public override void Close()
    {
#if POB_TCP_DEBUG
      Console.Error.WriteLine("edge: {0}, Closing",this);
#endif
      bool shutdown = false;
      lock( _sync ) {
        if (!_is_closed) {
          _is_closed = true;
          shutdown = true;
          //Make sure to drop references to buffers
          _rec_state.Buffer = null; 
          _send_state.Buffer = null;
        }
      }
      if( shutdown ) {
        try {
          //We don't want any more data, but try
          //to send the stuff we've sent:
          if( _sock.Connected ) {
            _sock.Shutdown(SocketShutdown.Send);
          }
          else {
            //There is no need to shutdown a socket that
            //is not connected.
          }
        }
        catch(Exception) {
          //Console.Error.WriteLine("Error shutting down socket on edge: {0}\n{1}", this, ex);
        }
        finally {
          _sock.Close();
        }
      }
      //Don't hold the lock while we close:
      base.Close();
    }

    public override bool IsClosed
    {
      get { lock(_sync) { return _is_closed; } }
    }
    public override bool IsInbound
    {
      get { return inbound; }
    }

    protected readonly DateTime _create_dt;
    public override DateTime CreatedDateTime {
      get { return _create_dt; }
    }
    protected DateTime _last_out_packet_datetime;
    public override DateTime LastOutPacketDateTime {
      get { lock( _sync ) { return _last_out_packet_datetime; } }
    }
    /**
     * @param p the Packet to send
     * @throw EdgeException if we cannot send
     */
    //Here is the Select version
    public override void Send(ICopyable p)
    {
      if( p == null ) {
        throw new System.NullReferenceException(
           "TcpEdge.Send: argument can't be null");
      }

      lock( _sync ) {
#if POB_TCP_DEBUG
        Console.Error.WriteLine("edge: {0}, Entering Send",this);
#endif
        if( _is_closed ) {
          throw new EdgeException("Tried to send on a closed socket");
        }
        _last_out_packet_datetime = DateTime.UtcNow;
#if POB_TCP_DEBUG
        Console.Error.WriteLine("edge: {0}, About to enqueue packet of length: {1}",
                          this, p.Length);
#endif
        //Else just queue up the packet
	if( _packet_queue.Count < MAX_QUEUE_SIZE ) {
          //Don't queue indefinitely...
          _packet_queue.Enqueue(p);
          Interlocked.Increment( ref _queued_packets );
	}
	//Console.Error.WriteLine("Queue length: {0}", _packet_queue.Count);
      }
#if POB_TCP_DEBUG
      Console.Error.WriteLine("Setting NeedToSend");
#endif
      NeedToSend = (Thread.VolatileRead(ref _queued_packets ) > 0);
#if POB_TCP_DEBUG
      Console.Error.WriteLine("Need to send: {0}", NeedToSend);
      Console.Error.WriteLine("edge: {0}, Leaving Send",this);
#endif
    }

    public override Brunet.TransportAddress.TAType TAType
    {
      get { return Brunet.TransportAddress.TAType.Tcp; }
    }

    protected readonly TransportAddress _local_ta;
    public override Brunet.TransportAddress LocalTA
    {
      get { return _local_ta; }
    }
    /*
     * If this is an inbound link
     * then the LocalTA is not ephemeral
     */
    public override bool LocalTANotEphemeral {
      get { return IsInbound; }
    }
    
    protected readonly TransportAddress _remote_ta;
    public override Brunet.TransportAddress RemoteTA
    {
      get { return _remote_ta; }
    }

    /*
     * If this is an outbound link, which is to say
     * not inbound, then the RemoteTA is not ephemeral
     */
    public override bool RemoteTANotEphemeral {
      get { return !IsInbound; }
    }
    
    /**
     * This should only be called from one thread inside the TcpEdgeListener.
     * The only thread synchronization in here is on the packet queue, so if
     * this method is called from multiple threads bad things could happen.
     */
    public void DoSend(BufferAllocator buf)
    {
      try {
        if( _send_state.Length == 0 ) {
          /**
           * It's time to write into a new buffer:
           */
          int written = WritePacketsInto(buf); 
          int sent = _sock.Send( buf.Buffer, buf.Offset, written, SocketFlags.None);
          //Now we have sent, let's see if we have to wait to send more:
          if( sent <= 0 ) {
            //This is the case of the Edge closing.
            throw new EdgeException("Edge is closed");
          }
          if( sent < written ) {
            //We couldn't send the whole buffer, save some for later:
            _send_state.Length = written - sent;
#if COPY_PACKETS
            _send_state.Buffer = new byte[ _send_state.Length ];
            _send_state.Offset = 0;
            Array.Copy(buf.Buffer, buf.Offset + sent,
                       _send_state.Buffer, _send_state.Offset,
                       _send_state.Length );
#else
            _send_state.Buffer = buf.Buffer;
            _send_state.Offset = buf.Offset + sent;
            //Don't overwrite the data we have here:
            buf.AdvanceBuffer( written );
#endif
          }
          else {
            /*
             * We never touch _send_state so the Length is still 0.
             */
          }
        }
        else {
          //There is an old write pending:
          int sent = _sock.Send(_send_state.Buffer,
                                _send_state.Offset,
                                _send_state.Length,
                                SocketFlags.None);
          if( sent > 0 ) {
            _send_state.Offset += sent;
            _send_state.Length -= sent;
          }
          else {
            //The edge is now closed.
            throw new EdgeException("Edge is closed");
          }
        }

        /**
         * Now set NeedToSend to the correct values
         */
        if( _send_state.Length == 0 ) {
          //We have sent all we need to, don't keep a reference around
          _send_state.Buffer = null;
          //We only have more to send if the packet queue is not empty
          NeedToSend = (Thread.VolatileRead(ref _queued_packets ) > 0);
        }
        else {
          //We definitely have more bytes to send
          NeedToSend = true;
        }
      }
      catch {
        Close();
        throw new EdgeException("Edge is closed");
      }
    }

    /**
     * In the select implementation, the TcpEdgeListener
     * will tell a socket when to do a read.
     * Get at most one packet out.
     */
    public void DoReceive(BufferAllocator buf)
    {
      MemBlock p = null;
      try {
        //Reinitialize the rec_state
        if( _rec_state.Buffer == null ) {
          _rec_state.Reset(buf.Buffer, buf.Offset, 2, true);
#if !COPY_PACKETS
          buf.AdvanceBuffer(2);
#endif
        }
        int got = _sock.Receive(_rec_state.Buffer,
                                _rec_state.LastReadOffset,
                                _rec_state.LastReadLength,
                                SocketFlags.None);
        if( got == 0 ) {
          throw new EdgeException("Got zero bytes, this edge is closed");  
        }
        _rec_state.LastReadOffset += got;
        _rec_state.LastReadLength -= got;

        if( _rec_state.LastReadLength == 0 ) {
          //Something is ready to parse
          if( _rec_state.ReadingSize ) {
            short size = NumberSerializer.ReadShort(_rec_state.Buffer, _rec_state.Offset);
            if( size < 0 ) { Console.Error.WriteLine("ERROR: negative packet size: {0}", size); }
            _rec_state.Reset(buf.Buffer, buf.Offset, size, false);
#if !COPY_PACKETS
            buf.AdvanceBuffer(size);
#endif

            if( _sock.Available > 0 ) {
              //Now recursively try to get the payload:
              DoReceive(buf);
            }
          } else {
            //We are reading a whole packet:
#if COPY_PACKETS
            p = MemBlock.Copy(_rec_state.Buffer, _rec_state.Offset, _rec_state.Length);
#else
            p = MemBlock.Reference(_rec_state.Buffer, _rec_state.Offset, _rec_state.Length);
#endif
            //Reinitialize the rec_state
            _rec_state.Buffer = null;
          }
        }
        else {
          //There is more to read, we have to wait until it is here!
#if COPY_PACKET
          _rec_state.CopyBuffer();
#endif
        }
        if( p != null ) {
          //We don't hold the lock while we announce the packet
          ReceivedPacketEvent(p);
        }
      }
      catch {
        Close();
      }
    }

    /**
     * Dequeue packets from the packet queue and write them into
     * this BufferAllocator.
     * @return the total number of bytes written
     */
    protected int WritePacketsInto(BufferAllocator buf) {
      int written = 0;
      int current_offset = buf.Offset;
      lock(_sync) {
        /*
         * Let's try to get as many packets as will fit into
         * a buffer written:
         */
        bool cont_writing = (_packet_queue.Count > 0);
        while( cont_writing ) {
          //It is time to get a new packet
          ICopyable p = (ICopyable)_packet_queue.Dequeue();
          cont_writing = (Interlocked.Decrement( ref _queued_packets ) > 0);
          /*
           * Write the packet first so we can see how long it is, then
           * we write that length into the buffer
           */
          short p_length = (short)p.CopyTo( buf.Buffer, 2 + current_offset );
          //Now write into this buffer:
          NumberSerializer.WriteShort(p_length, buf.Buffer, current_offset);
          int current_written = 2 + p_length;
          current_offset += current_written;
          written += current_written;
          
          if( cont_writing ) {
            ICopyable next = (ICopyable)_packet_queue.Peek();
            if( next.Length + written > buf.Capacity ) {
              /* 
               * There is no room for the next packet, just stop now
               */
              cont_writing = false;
            }
          }
        }
      }
      return written;
    }
  }
}
