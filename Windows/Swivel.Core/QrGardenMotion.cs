using System;

namespace Swivel.Core;

// The camera angles and structure fade all derive from this one spring.
public sealed class QrGardenMotion
{
    private readonly MotionSpring turn=new(0,.75,1);
    private double fromYaw=45,fromElevation=32,toYaw=0,toElevation=90;private bool canonical=true;
    public double Yaw{get;private set;}=45;
    public double Elevation{get;private set;}=32;
    public double StructureOpacity=>1-Smooth((Elevation-32)/58,.45,.9);
    public bool Moving=>!turn.Settled;
    private static double Smooth(double value,double low=0,double high=1)
    {double t=Math.Clamp((value-low)/(high-low),0,1);return t*t*(3-2*t);}
    public void Turn(bool top,bool reduced)
    {
        if(canonical)turn.Target=top?1:0; // Normal reversals preserve spring position and velocity.
        else
        {
            // A manually chosen pose starts a new path to the requested canonical view.
            fromYaw=Yaw;fromElevation=Elevation;toYaw=top?0:45;toElevation=top?90:32;
            turn.Target=0;turn.Snap();turn.Target=1;
        }
        if(reduced||(Yaw==(top?0:45)&&Elevation==(top?90:32)))Stop();else Apply();
    }
    public void SetView(double yaw,double elevation)
    {
        if(!double.IsFinite(yaw)||yaw is <0 or >360||!double.IsFinite(elevation)||elevation is <20 or >90)throw new ArgumentOutOfRangeException();
        turn.Target=0;turn.Snap();canonical=false;fromYaw=toYaw=Yaw=yaw;fromElevation=toElevation=Elevation=elevation;
        Normalize();
    }
    public void Advance(double seconds,bool reduced=false)
    {
        if(!double.IsFinite(seconds)||seconds<0)throw new ArgumentOutOfRangeException(nameof(seconds));
        if(reduced)Stop();else{turn.Advance(seconds);Apply();Normalize();}
    }
    public void Stop(){turn.Snap();Apply();Normalize();}
    private void Apply()
    {double t=Smooth(turn.Value);Yaw=fromYaw+(toYaw-fromYaw)*t;Elevation=fromElevation+(toElevation-fromElevation)*t;}
    private void Normalize()
    {
        if(canonical||!turn.Settled)return;
        bool top=Yaw==0&&Elevation==90,scene=Yaw==45&&Elevation==32;if(!top&&!scene)return;
        fromYaw=45;fromElevation=32;toYaw=0;toElevation=90;turn.Target=top?1:0;turn.Snap();canonical=true;
    }
}
