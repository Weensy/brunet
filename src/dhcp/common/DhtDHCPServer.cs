#define DHCP_DEBUG
using System;
using System.Text;
using System.IO;

using System.Globalization;
using System.Collections;
using System.Runtime.Remoting.Lifetime;

using System.Xml;
using System.Xml.Serialization;

using Brunet;
using Brunet.Dht;

namespace Ipop {
  public class DhtDHCPServer: DHCPServer {
    protected FDht _dht;

    public DhtDHCPServer(byte []server_ip, FDht dht) {
      _dht = dht;
      this.ServerIP = server_ip;
      this.leases = new SortedList();
      //do not have to even be concerned about brunet namespace so far
    }

    protected override bool IsValidBrunetNamespace(string brunet_namespace) {
      return true;
    }

    protected override DHCPLease GetDHCPLease(string ipop_namespace) {
      if (leases.ContainsKey(ipop_namespace)) {
	return (DHCPLease) leases[ipop_namespace];
      }
      string ns_key = "dhcp:ipop_namespace:" + ipop_namespace;
      Console.Error.WriteLine("Searching for namespace key: {0} at time: {1}", ns_key, DateTime.Now);
      byte[] utf8_key = Encoding.UTF8.GetBytes(ns_key);
      //get a maximum of 1000 bytes only
      BlockingQueue[] q = _dht.GetF(utf8_key, 1000, null);
      //wait a second; we do expect to get atleast 1 result
      ArrayList [] results = BlockingQueue.ParallelFetchWithTimeout(q, 1000);

      ArrayList result = null;
      for (int i = 0; i < results.Length; i++) {
	ArrayList q_replies = results[i];
	foreach (RpcResult rpc_replies in q_replies) {
         //investigating individual results
         try{
           ArrayList rpc_result = (ArrayList) rpc_replies.Result;
           if (rpc_result == null || rpc_result.Count < 3) {
             continue;
           }
           result = rpc_result;
           break;
         } catch (Exception) {
           return null;
         }
       }
      }
      if (result == null) {
	return null;
      }
      ArrayList values = (ArrayList) result[0];
#if DHCP_DEBUG
      Console.Error.WriteLine("# of matching entries: " + values.Count);
#endif
      string xml_str = null;
      foreach (Hashtable ht in values) {
#if DHCP_DEBUG
        Console.Error.WriteLine(ht["age"]);
#endif
        byte[] data = (byte[]) ht["data"];
        xml_str = Encoding.UTF8.GetString(data);
#if DHCP_DEBUG
        Console.Error.WriteLine(xml_str);
#endif
        break;
      }
      if (xml_str == null) {
        return null;
      }
      XmlSerializer serializer = new XmlSerializer(typeof(IPOPNamespace));
      TextReader stringReader = new StringReader(xml_str);
      IPOPNamespace ipop_ns = (IPOPNamespace) serializer.Deserialize(stringReader);
      DHCPLease dhcp_lease = new DhtDHCPLease(_dht, ipop_ns);
      leases[ipop_namespace] = dhcp_lease;
#if DHCP_DEBUG
      Console.Error.WriteLine("Retrieved valid namespace information at time: {0}", DateTime.Now);
#endif
      return dhcp_lease;
    }

    protected override DHCPLeaseResponse GetLease(DHCPLease dhcp_lease, DecodedDHCPPacket packet) {
      DhtDHCPLeaseParam dht_param = new DhtDHCPLeaseParam(packet.yiaddr, packet.StoredPassword, DHCPCommon.StringToBytes(packet.NodeAddress, ':'));
      DHCPLeaseResponse ret = dhcp_lease.GetLease(dht_param);
      return ret;
    }
  }
}
