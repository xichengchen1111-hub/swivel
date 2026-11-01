using System;
using System.Collections.Generic;
using System.Globalization;

namespace Swivel.Core;

/// One bounded action-result sample, not a statement about consecutive task failures.
public sealed class TaskFailureHistory
{
    public const int MaxEvents=200;
    private readonly HashSet<Guid> failures=new();
    public int Count=>failures.Count;
    public void Observe(Guid instance,uint code)
    {
        if(instance!=Guid.Empty&&ConfigurationDiagnosis.Failure(code)&&failures.Count<MaxEvents)failures.Add(instance);
    }
    public static bool Instance(object? raw,out Guid value)
    {
        if(raw is Guid id){value=id;return id!=Guid.Empty;}
        if(raw is string text&&text.Length<=64&&Guid.TryParse(text,out value))return value!=Guid.Empty;
        value=Guid.Empty;return false;
    }
    public static bool Code(object? raw,out uint result)
    {
        switch(raw)
        {
            case uint value:result=value;return true;
            case int value:result=unchecked((uint)value);return true;
            case long value when value>=int.MinValue&&value<=uint.MaxValue:result=unchecked((uint)value);return true;
            case string text when text.Length<=16:
                if(text.StartsWith("0x",StringComparison.OrdinalIgnoreCase))return uint.TryParse(text.AsSpan(2),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out result);
                return uint.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out result);
            default:result=0;return false;
        }
    }
}
