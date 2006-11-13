using System;
using Brunet;
using Brunet.Dht;
using System.Text;
using System.Threading;
using System.Collections;
using System.Net.Sockets;
using System.Net;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Serialization;
using Mono.Security.Authenticode;

#if IPOP_LOG
using log4net;
using log4net.Config;
#endif

namespace Ipop {
  public class IPRouterConfig {
    public string ipop_namespace;
    public string brunet_namespace;
    public string dht_media;
    public string device;
    [XmlArrayItem (typeof(string), ElementName = "transport")]
    public string [] RemoteTAs;
    public EdgeListener [] EdgeListeners;
    public string IPConfig;
    public string DHCPServerIP;
    public string NodeAddress;
    public string Setup;
    public string Hostname;
    public string TapMAC;
    public StaticInfo StaticData;
    public DHCPInfo DHCPData;
  }

  public class StaticInfo {
    public string IPAddress;
    public string Netmask;
  }

  public class DHCPInfo {
    public string DHCPServerAddress;
    public string IPAddress;
    public string Netmask;
    public string Password;
  }

  public class EdgeListener {
    [XmlAttribute]
    public string type;
    public string port;
    public string port_hi;
    public string port_low;
  }

  public class IPRouter {
#if IPOP_LOG
    private static readonly log4net.ILog _log =
    log4net.LogManager.GetLogger(System.Reflection.MethodBase.
                                 GetCurrentMethod().DeclaringType);
#endif
    //if debugging information is needed
    private static bool debug;
    //the class modeling the ethernet;
    private static Ethernet ether;
    //status 0 = inactive, 1 = active
    private static int status;

    //0 = inactive, 1 = active;
    private static int dhcp_client_status = 0;

    //Configuration Data
    private static IPRouterConfig config;

    private static ArrayList RemoteTAs;

    private static OSDependent routines;
    private static ArrayList Nameservers;
    private static string Virtual_IPAddress;
    private static string Netmask;
    private static string ConfigFile;

    private static DHCPClient dhcp_client;

    private static BrunetTransport brunet;
    private static RoutingTable routes;

/*  Generic */

    private static void ReadConfiguration(string configFile) {
      XmlSerializer serializer = new XmlSerializer(typeof(IPRouterConfig));
      FileStream fs = new FileStream(configFile, FileMode.Open);
      config = (IPRouterConfig) serializer.Deserialize(fs);
      RemoteTAs = new ArrayList();
      foreach(string TA in config.RemoteTAs) {
        TransportAddress ta = new TransportAddress(TA);
        RemoteTAs.Add(ta);
      }
      fs.Close();
      if(config.NodeAddress == null) {
        RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
        byte [] temp = new byte[20];
        rng.GetBytes(temp);
        config.NodeAddress = DHCPCommon.BytesToString(temp, ':');
        UpdateConfiguration(configFile);
      }
      if(config.Setup == null) {
        config.Setup = "auto";
      }
    }

    private static void UpdateConfiguration(string configFile) {
      FileStream fs = new FileStream(configFile, FileMode.OpenOrCreate, 
        FileAccess.Write);
      XmlSerializer serializer = new XmlSerializer(typeof(IPRouterConfig));
      serializer.Serialize(fs, config);
      fs.Close();
    }

    private static BigInteger GetHash(IPAddress addr) {
       //Console.WriteLine("The IP addr: {0}", addr);
       HashAlgorithm hashAlgo = HashAlgorithm.Create();
       //hashAlgo.HashSize = AHAddress.MemSize;
       //Console.WriteLine("hash size: {0}" + hashAlgo.HashSize);
       byte[] hash = hashAlgo.ComputeHash(addr.GetAddressBytes());
       hash[Address.MemSize -1] &= 0xFE;
       return new BigInteger(hash);
    }

    static BrunetTransport Start() {
      if (brunet != null) {
	//disconnect what may have already started
	brunet.Node.Disconnect();
      }
      //Should be active now
      status = 1;
      //Setup TAAuthorizer
      byte [] netmask = DHCPCommon.StringToBytes(Netmask, '.');
      int nm_value = (netmask[0] << 24) + (netmask[1] << 16) +
        (netmask[2] << 8) + netmask[3];
      int value = 0;
      for(value = 0; value < 32; value++)
        if((1 << value) == (nm_value & (1 << value)))
          break;
      value = 32 - value;
      TAAuthorizer ta_auth = new NetmaskTAAuthorizer(
        IPAddress.Parse(Virtual_IPAddress), value,
        TAAuthorizer.Decision.Deny, TAAuthorizer.Decision.Allow);
      //local node
      AHAddress us = new AHAddress(GetHash(IPAddress.Parse(Virtual_IPAddress)));
      Console.WriteLine("Generated address: {0}", us);
      //AHAddress us = new AHAddress(new BigInteger(Int32.Parse(args[1])));
      Node tmp_node = new StructuredNode(us, config.brunet_namespace);

      //Where do we listen:
      IPAddress[] tas = routines.GetIPTAs(Virtual_IPAddress);
#if IPOP_LOG
	string listener_log = "BeginListener::::";
#endif

      foreach(EdgeListener item in config.EdgeListeners) {
        int port = 0;
        if(item.port_hi != null && item.port_low != null && item.port == null) {
          int port_hi = Int32.Parse(item.port_hi);
          int port_low = Int32.Parse(item.port_low);
          Random random = new Random();
          port = (random.Next() % (port_hi - port_low)) + port_low;
        }
        else
            port = Int32.Parse(item.port);
#if IPOP_LOG
	listener_log += item.type + "::::" + port + "::::";
#endif	
        if (item.type =="tcp") { 
            tmp_node.AddEdgeListener(new TcpEdgeListener(port, tas, 
              ta_auth));
        }
        else if (item.type == "udp") {
            tmp_node.AddEdgeListener(new UdpEdgeListener(port , tas, 
              ta_auth));
        }
        else if (item.type == "udp-as") {
            tmp_node.AddEdgeListener(new ASUdpEdgeListener(port, tas, 
              ta_auth));
        }
        else {
          throw new Exception("Unrecognized transport: " + item.type);
        }
      }
#if IPOP_LOG
      listener_log += "EndListener";
#endif
      //Here is where we connect to some well-known Brunet endpoints
      tmp_node.RemoteTAs = RemoteTAs;


#if IPOP_LOG
      _log.Debug("IGNORE");
      _log.Debug(tmp_node.Address + "::::" + DateTime.UtcNow.Ticks
                 + "::::Connecting::::" + System.Net.Dns.GetHostName() + "::::" + listener_log);
#endif 
      tmp_node.Connect();
      System.Console.WriteLine("Called Connect");

      brunet = new BrunetTransport(tmp_node);

      //subscribe to the IP protocol packet
      IPPacketHandler ip_handler = new IPPacketHandler(ether, debug, 
						       IPAddress.Parse(Virtual_IPAddress));
      brunet.Resubscribe(ip_handler);
      return brunet;
    }
    static BrunetTransport RandomStart() {
      //Should be active now
      status = 1;
      
      //local node
      Random my_rand = new Random();
      byte[] bin_address = new byte[Address.MemSize];
      my_rand.NextBytes(bin_address);
      bin_address[Address.MemSize -1] &= 0xFE;      
      
      AHAddress us = new AHAddress(bin_address);
      Console.WriteLine("Generated address: {0}", us);
      //AHAddress us = new AHAddress(new BigInteger(Int32.Parse(args[1])));
      Node tmp_node = new StructuredNode(us, config.brunet_namespace);

      //Where do we listen:
      IPAddress[] tas = routines.GetIPTAs(Virtual_IPAddress);
#if IPOP_LOG
	string listener_log = "BeginListener::::";
#endif

      foreach(EdgeListener item in config.EdgeListeners) {
        int port = 0;
        if(item.port_hi != null && item.port_low != null && item.port == null) {
          int port_hi = Int32.Parse(item.port_hi);
          int port_low = Int32.Parse(item.port_low);
          Random random = new Random();
          port = (random.Next() % (port_hi - port_low)) + port_low;
        }
        else
            port = Int32.Parse(item.port);
#if IPOP_LOG
	listener_log += item.type + "::::" + port + "::::";
#endif	
        if (item.type =="tcp") { 
	  tmp_node.AddEdgeListener(new TcpEdgeListener(port, tas)); 
        }
        else if (item.type == "udp") {
	  tmp_node.AddEdgeListener(new UdpEdgeListener(port , tas)); 
        }
        else if (item.type == "udp-as") {
	  tmp_node.AddEdgeListener(new ASUdpEdgeListener(port, tas));
        }
        else {
          throw new Exception("Unrecognized transport: " + item.type);
        }
      }
#if IPOP_LOG
      listener_log += "EndListener";
#endif
      //Here is where we connect to some well-known Brunet endpoints
      tmp_node.RemoteTAs = RemoteTAs;

      //following line of code enables DHT support inside the IPRouter
      Dht dht = null;
      if (config.dht_media == null || config.dht_media.Equals("disk")) {
        dht = new Dht(tmp_node, EntryFactory.Media.Disk);
      } else if (config.dht_media.Equals("memory")) {
        dht = new Dht(tmp_node, EntryFactory.Media.Memory);
      }
      
#if IPOP_LOG
      _log.Debug("IGNORE");
      _log.Debug(tmp_node.Address + "::::" + DateTime.UtcNow.Ticks
                 + "::::Connecting::::" + System.Net.Dns.GetHostName() + "::::" + listener_log);
#endif   
      tmp_node.Connect();
      System.Console.WriteLine("Called Connect");

      BrunetTransport brunet = new BrunetTransport(tmp_node, dht);
      return brunet;
    }

    private static void ProcessDHCP(object arg)
    {
      byte[] buffer = (byte[]) arg;
      DHCPPacket dhcpPacket = new DHCPPacket(buffer);
      /* Create new DHCPPacket, parse the bytes, add relevant data, 
          and send to DHCP Server */
      dhcpPacket.DecodePacket();
      dhcpPacket.decodedPacket.brunet_namespace = config.brunet_namespace;
      dhcpPacket.decodedPacket.ipop_namespace = config.ipop_namespace;

      if (config.IPConfig == "dhcp-dht") {
	byte[] temp = new byte[Address.MemSize];
	brunet.Node.Address.CopyTo(temp);
	dhcpPacket.decodedPacket.NodeAddress = DHCPCommon.BytesToString(temp, ':');
	if (config.DHCPData != null) {
	  dhcpPacket.decodedPacket.yiaddr = IPAddress.Parse(config.DHCPData.IPAddress).GetAddressBytes();
	  dhcpPacket.decodedPacket.StoredPassword = config.DHCPData.Password;
	}
      } else if (config.IPConfig == "dhcp") {
	dhcpPacket.decodedPacket.NodeAddress = config.NodeAddress;
      }

      /* DHCP Server returns our incoming packet, which we decode, if it
          is successful, we continue, otherwise we fail and print out a message */
      DHCPPacket returnPacket = null;
      string response = null;
      try {
        returnPacket = new DHCPPacket(
				      dhcp_client.SendMessage(dhcpPacket.decodedPacket));
      }
      catch (Exception e)
      {
        System.Console.WriteLine(e);
        response = e.ToString();
      }
      if(returnPacket != null &&
        returnPacket.decodedPacket.return_message == "Success") {
        /* Add nameservers if it doesn't contain it already - this is */
        /* deprecated and will removed soon */
        if(!returnPacket.decodedPacket.options.Contains(6)) {
          DHCPOption option = new DHCPOption();
          option.type = 6;
          option.length = Nameservers.Count * 4;
          option.encoding = "int";
          option.byte_value = new byte[option.length];
          int i = 0, ci = 4;

          foreach(string item0 in Nameservers) {
            byte [] temp = DHCPCommon.StringToBytes(item0, '.');
            for(; i < ci; i++)
              option.byte_value[i] = temp[i%4];
            ci += 4;
          }
          returnPacket.decodedPacket.options.Add(option.type, option);
        }
         /* Expected removal date November 1st */

        /* Convert the packet into byte format, run Arp and Route updater */
	returnPacket.EncodePacket();
	ether.SendPacket(returnPacket.packet, 0x800);
        /* Do we have a new IP address, if so (re)start Brunet */
	string newAddress = DHCPCommon.BytesToString(
						     returnPacket.decodedPacket.yiaddr, '.');
        String newNetmask = DHCPCommon.BytesToString(((DHCPOption) returnPacket.
						      decodedPacket.options[1]).byte_value, '.');
	Netmask = newNetmask;
	Virtual_IPAddress = newAddress;
	config.DHCPData.IPAddress = Virtual_IPAddress;
	config.DHCPData.Netmask = Netmask;
	config.DHCPData.Password = returnPacket.decodedPacket.StoredPassword;

	UpdateConfiguration(ConfigFile);

	if(config.Setup == "auto") {
	  if(config.Hostname == null)
	    routines.SetHostname(routines.DHCPGetHostname(Virtual_IPAddress));
	  else
	    routines.SetHostname(config.Hostname);
	}
	if (Virtual_IPAddress == null || Virtual_IPAddress != newAddress ||
	    newNetmask != Netmask) {
	  
	}
	if (config.IPConfig == "dhcp") {
          brunet = Start();
          routes = new RoutingTable();
	} else if (config.IPConfig == "dhcp-dht") {
	  //we will need to register a few handlers
	  if (config.IPConfig == "dhcp-dht") {
	    IPPacketHandler ip_handler = new IPPacketHandler(ether, debug,
							     IPAddress.Parse(Virtual_IPAddress));
	    brunet.Resubscribe(ip_handler);
	  }
	}
      }
      else {
        if (returnPacket != null)
          response = returnPacket.decodedPacket.return_message;
        /* Not a success, means we can't continue on, sorry, 
           print the friendly server message */
        Console.WriteLine("The DHCP Server has a message to share with you...");
        Console.WriteLine("\n" + response);
        Console.WriteLine("\nSorry, this program will sleep and try again later.");
        Thread.Sleep(600);
      }
      //reset the status back to "inactive"
      dhcp_client_status = 0;
    }



    static void Main(string []args) {
      //configuration file 
      if (args.Length < 1) {
        Console.WriteLine("please specify the configuration file name...");
        Environment.Exit(0);
      }
      ConfigFile = args[0];

#if IPOP_LOG
      if (args.Length < 2) {
        Console.WriteLine("please specify the full path to the Logger " + 
          "configuration file...");
        Environment.Exit(1);
      }
      XmlConfigurator.Configure(new System.IO.FileInfo(args[1]));
#endif

      ReadConfiguration(ConfigFile);
      if (args.Length == 3) {
        debug = true;
      } else {
        debug = false;
      }

      routines = new OSDependent();
      System.Console.WriteLine("IPRouter starting up...");
      if(config.TapMAC != null && config.Setup == "manual")
        ether = new Ethernet(config.device, config.TapMAC,
          "FE:FD:00:00:00:00");
      else
        ether = new Ethernet(config.device, "FE:FD:00:00:00:01", 
          "FE:FD:00:00:00:00");
      if (ether.Open() < 0) {
        Console.WriteLine("unable to set up the tap");
        return;
      }

      brunet = null;
      routes = null;

      if(config.Setup == "auto")
        routines.SetTapMAC(config.device);

      if(config.IPConfig == "static")
      {
        Virtual_IPAddress = config.StaticData.IPAddress;
        Netmask = config.StaticData.Netmask;
        if(config.Setup == "auto") {
          routines.SetTapDevice(config.device, Virtual_IPAddress, Netmask);
          if(config.Hostname != null)
            routines.SetHostname(config.Hostname);
        }
        //setup Brunet node
        brunet = Start();
        //build a new routes table and populate it artificially
        routes = new RoutingTable();
      } else if (config.IPConfig == "dhcp") {
	//in this case we start the Brunet node upfront
        Nameservers = routines.GetNameservers();
        dhcp_client = new SoapDHCPClient(config.DHCPData.DHCPServerAddress);
        if(config.DHCPData.IPAddress != null && config.DHCPData.Netmask != null) {
          Virtual_IPAddress = config.DHCPData.IPAddress;
          Netmask = config.DHCPData.Netmask;
          brunet = Start();
          routes = new RoutingTable();
          if(config.Setup == "auto") {
            if(config.Hostname == null)
              routines.SetHostname(routines.DHCPGetHostname(Virtual_IPAddress));
            else
              routines.SetHostname(config.Hostname);
          }
        }
        else {
          Virtual_IPAddress = null;
          Netmask = null;
        }
      } else if (config.IPConfig == "dhcp-dht") {
	Nameservers = routines.GetNameservers();
	brunet = RandomStart();
	dhcp_client = new DhtDHCPClient(brunet.Dht);
      }
      // else wait for dhcp packet below

      //start the asynchronous communication now
      while(true) {
        //now the packet
        byte [] packet = ether.ReceivePacket();
        //Console.WriteLine("read a packet of length: {0}", packet.Length);
        if (packet == null) {
          Console.WriteLine("error reading packet from ethernet");
          continue;
        }

        /*  ARP Packet Handler */
        int type = (packet[12] << 8) + packet[13];
        byte [] buffer = null;

        if(type == 0x806 || type == 0x800) {
          buffer =  new byte[packet.Length - 14];
          Array.Copy(packet, 14, buffer, 0, buffer.Length);
        }
        else
          continue;

        if(type == 0x806) {
          string IP = buffer[24].ToString() + "." + buffer[25].ToString() + "." 
            + buffer[26].ToString() + "." + buffer[27].ToString();
          if(Virtual_IPAddress == IP)
            continue;
          /* Set HWAddr of dest to FE:FD:00:00:00:00 */
          buffer[7] = 2;
          byte [] temp;
          if(buffer[14] == 0)
            temp = new byte[] {0xFF, 0xFF, 0xFF, 0xFF};
          else
            temp = new byte[] {buffer[14], buffer[15], buffer[16], buffer[17]};
          buffer[8] = 0xFE;
          buffer[9] = 0xFD;
          buffer[10] = 0x00;
          buffer[11] = 0x00;
          buffer[12] = 0x00;
          buffer[13] = 0x00;

          for(int i = 0; i <= 3; i++)
            buffer[14+i] = buffer[24+i];

          if(config.TapMAC != null && config.Setup == "manual") {
            byte [] temp1 = DHCPCommon.HexStringToBytes(config.TapMAC, ':');
            for(int i = 0; i <= 5; i ++)
              buffer[18+i] = temp1[i];
          }
          else {
            buffer[18] = 0xFE;
            buffer[19] = 0xFD;
            buffer[20] = 0x00;
            buffer[21] = 0x00;
            buffer[22] = 0x00;
            buffer[23] = 0x01;
          }

          for(int i = 0; i <= 3; i++)
            buffer[24+i] = temp[i];
          ether.SendPacket(buffer, 0x806);
          continue;
        }

        /*  End Arp */

        IPAddress destAddr = IPPacketParser.DestAddr(buffer);
        IPAddress srcAddr = IPPacketParser.SrcAddr(buffer);

        int destPort = (buffer[22] << 8) + buffer[23];
        int srcPort = (buffer[20] << 8) + buffer[21];
        int protocol = buffer[9];

        if (debug) {
          Console.WriteLine("Outgoing {0} packet::IP src: {1}:{2}," + 
            "IP dst: {3}:{4}", protocol, srcAddr, srcPort, destAddr,
            destPort);
        }

        if(srcPort == 68 && destPort == 67 && protocol == 17 && 
          (config.IPConfig == "dhcp" || config.IPConfig == "dhcp-dht")) {
          if (debug) 
            Console.WriteLine("DHCP Packet");
	  Console.WriteLine("dhcp packet");
	  if (dhcp_client_status == 1) {
	    continue;
	  }
	  dhcp_client_status = 1;
	  ThreadPool.QueueUserWorkItem(new WaitCallback(ProcessDHCP), (object) buffer);
	  continue;
        }

        if(status == 1) {
          AHAddress target = (AHAddress) routes.SearchRoute(destAddr);
          if (target == null) {
            target = new AHAddress(GetHash(destAddr));
            routes.AddRoute(destAddr, target);
          }

          if (debug) {
            Console.WriteLine("Brunet destination ID: {0}", target);
          }
          brunet.SendPacket(target, buffer);
        }
      }
    }
  }
}
