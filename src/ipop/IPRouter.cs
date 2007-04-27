using System;
using Brunet;
using Brunet.Dht;
using System.Text;
using System.Threading;
using System.Collections;
using System.Net.Sockets;
using System.Net;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Ipop {
  public class IPRouter {
    //if debugging information is needed
    private static bool debug;
    //the class modeling the ethernet;
    private static Ethernet ether;
    private static bool ethernet;
    //Configuration Data
    private static IPRouterConfig config;
    private static ArrayList RemoteTAs;
    private static string ConfigFile;
    public static NodeMapping node;
    private static byte []routerMAC = new byte[]{0xFE, 0xFD, 0, 0, 0, 0};
    private static bool in_dht;

/*  DHT added code */

    private static DHCPClient dhcpClient;
    private static Cache brunet_arp_cache;
    private static RouteMissHandler route_miss_handler;

/*  Generic */
    private static void BrunetStart() {
      node.brunet = new BrunetTransport(ether, config.brunet_namespace,
        node, config.EdgeListeners, config.DevicesToBind, RemoteTAs, debug,
        config.dht_media);
      brunet_arp_cache = new Cache(100);
      RouteMissHandler.RouteMissDelegate dlgt =
        new RouteMissHandler.RouteMissDelegate(RouteMissCallback);
      route_miss_handler = new RouteMissHandler(node.brunet.dht, node.ipop_namespace, dlgt);
    }

    private static bool ARPHandler(byte []packet) {
      string TargetIPAddress = "", SenderIPAddress = "";
      for(int i = 0; i < 3; i++) { 
        TargetIPAddress += packet[24+i].ToString() + ".";
        SenderIPAddress += packet[14+i].ToString() + ".";
      }
      SenderIPAddress += packet[17].ToString();
      TargetIPAddress += packet[27].ToString();
      /* Must return nothing if the node is checking availability of IPs */
      /* Or he is looking himself up. */
      if((node.ip != null) && node.ip.Equals(IPAddress.Parse(TargetIPAddress)) ||
        SenderIPAddress.Equals("255.255.255.255") ||
        SenderIPAddress.Equals("0.0.0.0")) {
        return false;
      }

      byte [] replyPacket = new byte[packet.Length];
      /* Same base */
      Array.Copy(packet, 0, replyPacket, 0, 7);
      /* ARP Reply */
      replyPacket[7] = 2;
      /* Source MAC Address */
      Array.Copy(routerMAC, 0, replyPacket, 8, 6);
      /* Source IP Address */
      Array.Copy(packet, 24, replyPacket, 14, 4);
      /* Target MAC Address */
      byte []dstMACAddr = new byte[6];
      Array.Copy(packet, 8, dstMACAddr, 0, 6);
      Array.Copy(packet, 8, replyPacket, 18, 6);
      /* Target IP Address */
      if(packet[14] == 0) {
        for (int i = 0; i < 4; i++) {
          replyPacket[24+i] = 0xFF;
        }
      }
      else {
        Array.Copy(packet, 14, replyPacket, 24, 4);
      }
      ether.SendPacket(replyPacket, 0x806, dstMACAddr);
      return true;
    }

    private static void IPUpdate(object ip) {
      if(node.brunet.Update((string) ip.ToString())) {
        Console.Error.WriteLine("MY IP " + node.ip);
        config.AddressData.IPAddress = node.ip.ToString();
        config.AddressData.Netmask = node.netmask;
        config.AddressData.Password = node.password;
        IPRouterConfigHandler.Write(ConfigFile, config);
      }
      in_dht = false;
    }

    private static void ProcessDHCP(object buffero) {
      byte [] buffer = (byte []) buffero;
      DHCPPacket dhcpPacket = new DHCPPacket(buffer);
      /* Create new DHCPPacket, parse the bytes, add relevant data, 
          and send to DHCP Server */
      dhcpPacket.DecodePacket();
      dhcpPacket.decodedPacket.brunet_namespace = config.brunet_namespace;
      dhcpPacket.decodedPacket.ipop_namespace = config.ipop_namespace;
      dhcpPacket.decodedPacket.NodeAddress = node.nodeAddress;

      if (config.AddressData.IPAddress != null && config.AddressData.Password != null) {
        dhcpPacket.decodedPacket.yiaddr =
          IPAddress.Parse(config.AddressData.IPAddress).GetAddressBytes();
        dhcpPacket.decodedPacket.StoredPassword = node.password;
      }

      /* DHCP Server returns our incoming packet, which we decode, if it
          is successful, we continue, otherwise we fail and print out a message */
      DHCPPacket returnPacket = null;
      string response = null;
      try {
        returnPacket = new DHCPPacket(
          dhcpClient._dhcp_server.SendMessage(dhcpPacket.decodedPacket));
      }
      catch (Exception e) {
        response = e.ToString();
      }
      if(returnPacket != null &&
        returnPacket.decodedPacket.return_message == "Success") {
        // Close the IP Address refresher thread in Brunet if it is running
        if(config.AddressData.DhtDHCP) {
          node.brunet.InterruptRefresher();
        }
        /* Convert the packet into byte format, run Arp and Route updater */
        returnPacket.EncodePacket();
        ether.SendPacket(returnPacket.packet, 0x800, node.mac);
        /* Do we have a new IP address, if so (re)start Brunet */
        string newAddress = IPOP_Common.BytesToString(
          returnPacket.decodedPacket.yiaddr, '.');
        string newNetmask = IPOP_Common.BytesToString(((DHCPOption)
          returnPacket.decodedPacket.options[1]).byte_value, '.');
        if(node.ip == null || node.ip.ToString() != newAddress ||
          node.netmask !=  newNetmask) {
          if(!config.AddressData.DhtDHCP && !node.brunet.Update(newAddress)) {
            in_dht = false;
            return;
          }
          else {
            node.password = returnPacket.decodedPacket.StoredPassword;
            config.AddressData.Password = node.password;
            node.brunet.UpdateTAAuthorizer();
          }
          node.netmask = newNetmask;
          node.ip = IPAddress.Parse(newAddress);
	  Console.Error.WriteLine("Following DHCP my IP: {0}", node.ip);
          config.AddressData.IPAddress = newAddress;
          config.AddressData.Netmask = node.netmask;
          IPRouterConfigHandler.Write(ConfigFile, config);
        }
      }
      else {
        if (returnPacket != null)
          response = returnPacket.decodedPacket.return_message;
        /* Not a success, means we can't continue on, sorry, 
           print the friendly server message */
        Console.Error.WriteLine("The DHCP Server has a message to share with you...");
        Console.Error.WriteLine("\n" + response);
        Console.Error.WriteLine("\nSorry, this program will sleep and try again later.");
      }
      in_dht = false;
      return;
    }

    public static void RouteMissCallback(IPAddress ip, Address target) {
      brunet_arp_cache.Add(ip, target);
    }

    static void Main(string []args) {
      //configuration file 
      if (args.Length < 1) {
        Console.Error.WriteLine("please specify the configuration file name...");
        Environment.Exit(0);
      }
      ConfigFile = args[0];

      config = IPRouterConfigHandler.Read(ConfigFile, true);

      RemoteTAs = new ArrayList();
      foreach(string TA in config.RemoteTAs) {
        TransportAddress ta = TransportAddressFactory.CreateInstance(TA);
        RemoteTAs.Add(ta);
      }

      if(config.NodeAddress == null) {
        byte [] temp = IPOP_Common.GenerateAddress();
        config.NodeAddress = IPOP_Common.BytesToString(temp, ':');
        IPRouterConfigHandler.Write(ConfigFile, config);
      }

      OSDependent.Setup();
      if(OSDependent.OSVers == OSDependent.Linux) {
        Shutdown shut = new LinuxShutdown();
      }

      debug = false;
      if (args.Length == 2)
        debug = true;

      System.Console.Error.WriteLine("IPRouter starting up at time: {0}", DateTime.Now);
      ether = new Ethernet(config.device, routerMAC);
      if (ether.Open() < 0) {
        Console.Error.WriteLine("unable to set up the tap");
        return;
      }

      node = new NodeMapping();
      node.nodeAddress = config.NodeAddress;
      node.ipop_namespace = config.ipop_namespace;
      node.netmask = config.AddressData.Netmask;
      node.password = config.AddressData.Password;

      if(config.AddressData.IPAddress != null) {
        node.ip = IPAddress.Parse(config.AddressData.IPAddress);
      }

      BrunetStart();

      if(config.AddressData.DHCPServerAddress != null && !config.AddressData.DhtDHCP)
        dhcpClient = new SoapDHCPClient(config.AddressData.DHCPServerAddress);
      else {
        dhcpClient = new DhtDHCPClient(node.brunet.dht);
      }

      in_dht = false;
      ethernet = false;
      //start the asynchronous communication now
      while(true) {
        //now the packet
        int packet_size = 0;
        byte [] packet = ether.ReceivePacket(out packet_size);
        if (packet == null) {
          Console.Error.WriteLine("error reading packet from ethernet");
          continue;
        }
  /* We should really be checking each and every packet, but for simplicity sake
     we will only check until we are satisfied! */
        else if(!ethernet) {
          node.mac = new byte[6];
          Array.Copy(packet, 6, node.mac, 0, 6);
          ethernet = true;
        }

        int type = (packet[12] << 8) + packet[13];
        /*  ARP Packet Handler */
        byte [] buffer = null;

        if(type == 0x806 || type == 0x800) {
          buffer =  new byte[packet_size - 14];
          Array.Copy(packet, 14, buffer, 0, buffer.Length);
        }
        else
          continue;

        if(type == 0x806) {
          ARPHandler(buffer);
          continue;
        }

        /*  End Arp */

        /* else if(type == 0x800) */

        IPAddress srcAddr = IPPacketParser.SrcAddr(buffer);
        IPAddress destAddr = IPPacketParser.DestAddr(buffer);
        int destPort = (buffer[22] << 8) + buffer[23];
        int srcPort = (buffer[20] << 8) + buffer[21];
        int protocol = buffer[9];

        if (debug) {
          Console.Error.WriteLine("Outgoing {0} packet::IP src: {1}:{2}," + 
            "IP dst: {3}:{4}", protocol, srcAddr, srcPort, destAddr,
            destPort);
        }

        if(srcPort == 68 && destPort == 67 && protocol == 17) {
          if (debug)
            Console.Error.WriteLine("DHCP packet at time: {0}, status: {1}", DateTime.Now, in_dht);
          if(!in_dht) {
            in_dht = true;
            ThreadPool.QueueUserWorkItem(new WaitCallback(ProcessDHCP), (object) buffer);
          }
          continue;
        }

//         if(!srcAddr.Equals(IPAddress.Parse("0.0.0.0")) && !srcAddr.Equals(node.ip) && !in_dht) {
//           Console.Error.WriteLine("Switching IP Address " + node.ip + " with " + srcAddr);
//           in_dht = true;
//           ThreadPool.QueueUserWorkItem(new WaitCallback(IPUpdate), (object) srcAddr.ToString());
//           continue;
//         }

        AHAddress target = (AHAddress) brunet_arp_cache.Get(destAddr);
        if (target == null) {
          Console.Error.WriteLine("Incurring a route miss for virtual ip: {0}", destAddr);
          route_miss_handler.HandleRouteMiss(destAddr);
          continue;
        }
        if (debug) {
          Console.Error.WriteLine("Brunet destination ID: {0}", target);
        }
        node.brunet.SendPacket(target, buffer);
      }
    }
  }
}
