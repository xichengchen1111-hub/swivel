using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Swivel.Core;

public sealed class DoctorReportSnapshot
{
    public string Json{get;}
    public bool SingleSample{get;}
    internal DoctorReportSnapshot(string json,bool singleSample=false){Json=json;SingleSample=singleSample;}
}

// Fail-closed fact projection. Never render arbitrary source values with ToString.
// Only exact known schemas, finite numeric types and closed literal states survive.
public static class DoctorAssistantReport
{
    public const string Limit="The redacted Doctor snapshot exceeds its supported fact or JSON budget. Nothing was sent.";
    public sealed record Metric(string Kind,int Sample,object[] Values);
    public static DoctorReportSnapshot Capture(IReadOnlyList<DoctorFact> facts)=>new(Create(facts));
    public static string Create(IReadOnlyList<DoctorFact> facts)
    {
        if(facts.Count>512)throw new UserError(Limit);var metrics=new List<Metric>();int omitted=0,drive=0,process=0,adapter=0;
        static bool Numbers(object[] values,int start,int count,out object[] result)
        {
            result=Array.Empty<object>();if(values.Length!=start+count)return false;var list=new List<object>();
            for(int i=start;i<values.Length;i++)
            {
                double n=values[i] switch{int v=>v,uint v=>v,long v=>v,ulong v=>v,double v=>v,_=>double.NaN};
                if(!double.IsFinite(n)||n<0||n>1e12)return false;list.Add(n);
            }
            result=list.ToArray();return true;
        }
        static double N(object[] values,int i)=>(double)values[i];
        static bool Counters(object[] values,double max)=>values.All(x=>(double)x<=max&&Math.Floor((double)x)==(double)x);
        foreach(var fact in facts)
        {
            if(fact is null||fact.PrivateDetails||fact.Values is null){omitted++;continue;}
            var v=fact.Values;object[] data=Array.Empty<object>();string? kind=null;int sample=0;
            if(fact.Section=="Overview")
            {
                if(fact.Title=="System"&&fact.Format=="{0} · {1} · {2} logical processors"&&v.Length==3&&v[1] is string arch&&arch is "X64" or "Arm64" or "X86" or "Arm"&&Numbers(v,2,1,out data))
                {if(N(data,0)>=1&&Counters(data,4096)){kind="system_architecture_processors";data=new object[]{arch,data[0]};}}
                else if(fact.Title=="Uptime"&&fact.Format=="{0} hours since the last system start"&&Numbers(v,0,1,out data)&&N(data,0)<=876000)kind="uptime_hours";
                else if(fact.Title=="Memory"&&fact.Format=="{0} GB available of {1} GB; {2}% in use"&&Numbers(v,0,3,out data)&&N(data,2)<=100&&N(data,0)<=N(data,1)&&N(data,1)>0&&N(data,1)<=1e9)kind="physical_memory_available_total_gib_percent";
                else if(fact.Title=="Fixed Drives"&&fact.Format=="{0}: {1} GB available to this user of {2} GB"&&Numbers(v,1,2,out data)&&N(data,0)<=N(data,1)&&N(data,1)>0&&N(data,1)<=1e9){kind="fixed_drive_available_total_gib";sample=++drive;}
                else if(fact.Title=="Processes"&&fact.Format=="{0} processes reported; {1} unavailable or beyond the sampling limit"&&Numbers(v,0,2,out data)&&Counters(data,1e6)&&N(data,1)<=N(data,0))kind="process_count_unavailable_or_unsampled";
                else if(fact.Title=="Largest working sets"&&fact.Format=="{0}: {1} MB resident memory"&&Numbers(v,1,1,out data)&&N(data,0)<=1e9){kind="anonymous_working_set_mib";sample=++process;}
                else if(fact.Format=="{0}"&&v.Length==1&&v[0] is string state)
                {
                    if(fact.Title=="Power source"&&state is "On battery" or "Plugged in" or "Unknown"){kind="power_source";data=new object[]{state};}
                    else if(fact.Title=="Battery level"&&state is "No system battery" or "Unknown"){kind="battery_status";data=new object[]{state};}
                    else if(fact.Title=="Battery level"&&state.EndsWith('%')&&state.Length is >=2 and <=4&&state[..^1].All(char.IsAsciiDigit)&&int.TryParse(state[..^1],out int percent)&&percent<=100){kind="battery_percent";data=new object[]{percent};}
                    else if(fact.Title=="Battery saver"&&state=="On"){kind="battery_saver";data=new object[]{true};}
                }
            }
            else if(fact.Section=="Network & Proxy")
            {
                if(fact.Title=="Network adapters"&&fact.Format=="{0} adapter(s) reported; {1} active non-loopback adapter(s); {2} beyond the display limit."&&Numbers(v,0,3,out data)&&Counters(data,65536)&&N(data,1)<=N(data,0)&&N(data,2)<=N(data,0))kind="adapter_counts_total_active_omitted";
                else if(fact.Title=="Address configuration"&&fact.Format=="IPv4: {0} · IPv6: {1} · gateways: {2} · DNS servers: {3}"&&Numbers(v,0,4,out data)&&Counters(data,65536)){kind="anonymous_adapter_address_counts";sample=++adapter;}
                else if(fact.Format=="{0}"&&v.Length==1&&v[0] is string state)
                {
                    if(fact.Title=="IPv4 DHCP"&&state is "On" or "Off" or "Unavailable"){kind="dhcp_state";data=new object[]{state};}
                    else if(fact.Title=="Automatically detect proxy"&&state is "On" or "Off"){kind="proxy_autodetect";data=new object[]{state};}
                    else if(fact.Title is "Proxy setup script" or "Manual user proxy"&&state is "Configured" or "Not configured"){kind=fact.Title=="Proxy setup script"?"proxy_script_configured":"proxy_manual_configured";data=new object[]{state=="Configured"};}
                }
            }
            if(kind is null)omitted++;else metrics.Add(new(kind,sample,data));
        }
        string json=JsonSerializer.Serialize(new{format="Swivel.Doctor.Redacted.v1",observed_facts=facts.Count,omitted_facts=omitted,metrics,
            limitations=new[]{"Historical read-only snapshot, not a diagnosis or proof of Internet reachability.","Names, paths, drive letters, addresses, OS description and unknown fields are omitted. Samples are anonymous, not linked identities.","Working sets may share memory; logical drive capacity is not reclaimable storage. No automatic fixes are available."}},new JsonSerializerOptions{WriteIndented=true});
        if(Encoding.UTF8.GetByteCount(json)>32768)throw new UserError(Limit);return json;
    }
}
