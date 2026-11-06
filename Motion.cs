namespace IslandBar;

// Exact critically damped motion preserves position AND velocity on reversal.
sealed class Motion(double initial)
{
    public double Value { get; private set; } = initial;
    double _velocity;
    public void Step(double target, double seconds, double frequency)
    {
        double offset=Value-target, impulse=_velocity+frequency*offset, decay=Math.Exp(-frequency*seconds);
        Value=target+(offset+impulse*seconds)*decay;
        _velocity=(_velocity-frequency*impulse*seconds)*decay;
        if(Math.Abs(Value-target)<.03&&Math.Abs(_velocity)<.1){Value=target;_velocity=0;}
    }
    public bool AtRest(double target)=>Value==target&&_velocity==0;
    public void Snap(double target){Value=target;_velocity=0;}
}
