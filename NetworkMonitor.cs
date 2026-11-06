using System.Diagnostics;
using System.Net.NetworkInformation;

namespace IslandBar;
sealed record NetworkRate(double Down,double Up,string[] Adapters);
sealed class NetworkMonitor
{
    readonly Dictionary<string,(long Received,long Sent,long Time)> _previous=[];
    public NetworkRate Read()
    {
        double down=0,up=0;var names=new List<string>();var live=new HashSet<string>();long now=Stopwatch.GetTimestamp();
        bool Eligible(NetworkInterface a){try{return a.OperationalStatus==OperationalStatus.Up&&(a.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT)&&!new[]{"Virtual","Hyper-V","WireGuard","Tailscale","TAP-","Radmin","Npcap","WFP","QoS","Wi-Fi Direct"}.Any(v=>(a.Description+" "+a.Name).Contains(v,StringComparison.OrdinalIgnoreCase))&&a.GetIPProperties().UnicastAddresses.Count>0;}catch{return false;}}
        var candidates=NetworkInterface.GetAllNetworkInterfaces().Where(Eligible).ToArray();
        var gateways=candidates.Where(a=>a.GetIPProperties().GatewayAddresses.Any(g=>!g.Address.Equals(System.Net.IPAddress.Any)&&!g.Address.Equals(System.Net.IPAddress.IPv6Any))).ToArray();
        foreach(var adapter in gateways.Length>0?gateways:candidates)try{
            var stats=adapter.GetIPStatistics();live.Add(adapter.Id);names.Add(adapter.Name);
            if(_previous.TryGetValue(adapter.Id,out var last)){double seconds=(now-last.Time)/(double)Stopwatch.Frequency;if(seconds>0){down+=Math.Max(0,stats.BytesReceived-last.Received)/seconds;up+=Math.Max(0,stats.BytesSent-last.Sent)/seconds;}}
            _previous[adapter.Id]=(stats.BytesReceived,stats.BytesSent,now);
        }catch(NetworkInformationException){}
        foreach(string key in _previous.Keys.Where(k=>!live.Contains(k)).ToArray())_previous.Remove(key);
        return new(down,up,names.ToArray());
    }
}
