using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace IslandBar;

sealed record SensorReading(string Name,string Units,double Value,uint Id,uint Device);
sealed record PcState(double? CpuLoad,double? CpuTemperature,double? GpuLoad,double? GpuTemperature,double RamUsedGb,double RamTotalGb,double? VramUsedMb,string SensorSource,IReadOnlyList<SensorReading> Sensors);
sealed class HardwareMonitor
{
    [StructLayout(LayoutKind.Sequential)]struct MemoryStatus {public uint Length,Load;public ulong TotalPhys,AvailPhys,TotalPage,AvailPage,TotalVirtual,AvailVirtual,AvailExtended;}
    [DllImport("kernel32.dll")]static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")]static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    ulong _idle,_total;
    double? _cpu;
    public PcState Read()
    {
        if(GetSystemTimes(out ulong idle,out ulong kernel,out ulong user)){
            ulong total=kernel+user;if(_total!=0&&total>_total)_cpu=Math.Clamp(100d*(1-(idle-_idle)/(double)(total-_total)),0,100);_idle=idle;_total=total;
        }
        var memory=new MemoryStatus {Length=(uint)Marshal.SizeOf<MemoryStatus>()};GlobalMemoryStatusEx(ref memory);
        var sensors=ReadAfterburner();
        double? Sensor(uint id)=>sensors.FirstOrDefault(s=>s.Id==id)?.Value;
        var cpuTemps=sensors.Where(s=>s.Id==0x80).Select(s=>s.Value).ToArray();
        double? cpuTemperature=cpuTemps.Length==0?null:cpuTemps.Max();
        var totalCpu=sensors.FirstOrDefault(s=>s.Id==0x90&&s.Name.Equals("CPU usage",StringComparison.OrdinalIgnoreCase));
        return new(totalCpu?.Value??_cpu,cpuTemperature,Sensor(0x30),Sensor(0),
            (memory.TotalPhys-memory.AvailPhys)/1073741824d,memory.TotalPhys/1073741824d,Sensor(0x31),sensors.Count>0?"MSI Afterburner":"Windows",sensors);
    }
    static List<SensorReading> ReadAfterburner()
    {
        foreach(string name in new[]{"MAHMSharedMemory",@"Global\MAHMSharedMemory"})try{
            using var map=MemoryMappedFile.OpenExisting(name,MemoryMappedFileRights.Read);using var view=map.CreateViewAccessor(0,0,MemoryMappedFileAccess.Read);
            uint signature=view.ReadUInt32(0),version=view.ReadUInt32(4),header=view.ReadUInt32(8),count=view.ReadUInt32(12),size=view.ReadUInt32(16);
            if(signature!=0x4D41484D||version<0x20000||header<32||count>4096||size<1324||header+(long)count*size>view.Capacity)continue;
            long timestamp=view.ReadInt32(20);if(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-timestamp)>15)continue;
            var entries=new List<SensorReading>();var text=new byte[260];
            for(int i=0;i<count;i++){
                long offset=header+i*(long)size;float value=view.ReadSingle(offset+1300);if(!float.IsFinite(value)||Math.Abs(value)>1000000000)continue;
                view.ReadArray(offset,text,0,260);string source=Encoding.ASCII.GetString(text).Split('\0')[0];
                view.ReadArray(offset+260,text,0,260);string units=Encoding.Latin1.GetString(text).Split('\0')[0];
                entries.Add(new(source,units,value,view.ReadUInt32(offset+1320),view.ReadUInt32(offset+1316)));
            }
            return entries;
        }catch(Exception ex)when(ex is FileNotFoundException or UnauthorizedAccessException or IOException){}
        return [];
    }
}
