namespace EnbNext.Rendering;

/// <summary>Retained 669C70 / 498C30 / 498E00 trailing gauge controller.</summary>
public sealed class EnbRetailGaugeTrail
{
    private float _value, _target;
    public float Main { get; private set; }
    public float Trail { get; private set; }
    public bool Active { get; private set; }

    public void Reset(float value)
    {
        Main=Trail=_value=_target=Math.Clamp(value,0f,1f);
        Active=false;
    }

    public float Update(float value,uint elapsedMilliseconds)
    {
        value=Math.Clamp(value,0f,1f);
        if(value!=Main)
        {
            Main=value;
            if(value>Trail)
            {_target=_value=value;Active=false;}
            else
            {
                _target=value;
                Active=Math.Abs(_value-value)>0.0001f;
                if(value==0 && !Active)_value=0;
            }
        }
        if(Active)
        {
            float step=(float)elapsedMilliseconds*0.0001f;
            if(_target<=_value)
            {
                _value-=step;
                if(_value<=_target){_value=_target;Active=false;}
            }
            else
            {
                _value+=step;
                if(_target<=_value){_value=_target;Active=false;}
            }
        }
        Trail=Math.Clamp(_value,0f,1f);
        return Trail;
    }
}
