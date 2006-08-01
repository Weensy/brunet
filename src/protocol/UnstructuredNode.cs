/*
This program is part of BruNet, a library for the creation of efficient overlay
networks.
Copyright (C) 2005  University of California

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

//#define PRODUCTION
//to run the connecttester, make sure you change PRODUCTION to DEBUG
#define DEBUG  //Unstructured network is not formed

using System;
using System.Collections;

namespace Brunet
{

  /**
   * A node that makes connections on the unstructured system 
   */

  public class UnstructuredNode:Node
  {

    protected ConnectionOverlord _lco;
    protected ConnectionOverlord _uco;

#if PLAB_LOG
    override public BrunetLogger Logger{
      get{
        return _logger;
      }
      set
      {
        _logger = value;     
        //The connection table only has a logger in this case
        _connection_table.Logger = value;
        //_sco.Logger = value;
        foreach(EdgeListener el in _edgelistener_list) {
          el.Logger = value;
        }
      }
    }
#endif
    /**
     * Right now, this just asks if the main ConnectionOverlords
     * are looking for connections, with the assumption being
     * that if they are, we are not correctly connected.
     *
     * In the future, it might use a smarter algorithm
     */
    public override bool IsConnected {
      get {
        lock( _sync ) {
          //To be routable,
          return !(_lco.NeedConnection || _uco.NeedConnection);
        }
      }
    }

 
    public UnstructuredNode(AHAddress add):base(add)
    {

      /**
       * Here are the routers this node uses : 
       */
      ArrayList routers = new ArrayList();
      routers.Add(new RwpRouter());
      routers.Add(new RwtaRouter());

      SetRouters(routers);

      /**
       * Here are the ConnectionOverlords
       */ 
      _lco = new LeafConnectionOverlord(this);
      _uco = new UnstructuredConnectionOverlord(this);
#if PLAB_LOG
      //co.Logger = this.Logger;
#endif
      /**
       * Turn on some protocol support : 
       */
      /// Turn on Packet Forwarding Support :
      IAHPacketHandler h = new PacketForwarder(add);
      Subscribe(AHPacket.Protocol.Forwarding, h);
      /**
       * Here is how we handle ConnectToMessages : 
       */
      h = new CtmRequestHandler();
      Subscribe(AHPacket.Protocol.Connection, h);
    }

    /**
     * Connect to the network.  This informs all the ConnectionOverlord objects
     * to do their thing.
     */
    //ATTENTION: (Debug) To run the connecttester, do the following:
    override public void Connect()
    {
      StartAllEdgeListeners();
      _lco.IsActive = true;
      _uco.IsActive = true;
      
      _lco.Activate();
      _uco.Activate();
    }
    /**
     * This informs all the ConnectionOverlord objects
     * to not respond to loss of edges, then to issue close messages to all the edges
     * 
     */
    override public void Disconnect()
    {
      lock(_sync) {
        _lco.IsActive = false;
        _uco.IsActive = false;

        // close and remove all edges for the node
        //ConnectionTable.CloseAllEdges();

        // stop all edge listeners to prevent other nodes
        // from connecting to us
        StopAllEdgeListeners();
      }
    }
  }

}


