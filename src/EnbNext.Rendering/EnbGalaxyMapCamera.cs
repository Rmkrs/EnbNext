using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Galaxy camera owner: 005406A0/0053F9E0 and 007A5890..5F52.
/// Matrix rows are the transpose of retail's column-vector basis.</summary>
public sealed class EnbGalaxyMapCamera
{
    public const float HomePitch = 0.34906587f; // 00B79240
    public const float ClickPitch = 0.7853982f; // 00B7923C
    private static readonly float[] TransitionSpeed = [.2f,.8f,.9f,1,1,1,.9f,.6f,.4f,.1f,.05f];
    private Matrix4x4 _basis = Matrix4x4.Identity, _sourceBasis, _targetBasis;
    private Vector3 _sourceCenter, _targetCenter;
    private float _sourceDistance, _targetDistance;
    public Vector3 Center { get; private set; }
    public float Distance { get; private set; }
    public float Transition { get; private set; } = -1;
    public Matrix4x4 Basis => _basis;

    // 007BACD0: the larger authored viewport dimension spans [-.5,.5].
    public static Vector4 ProjectionBounds(Vector2 viewport)
    {
        float x = .5f, y = .5f;
        if (viewport.X > viewport.Y) y = (float)(.5 / viewport.X * viewport.Y);
        else x = (float)(.5 / viewport.Y * viewport.X);
        return new(-x,-y,x,y);
    }

    public void Frame(Vector3 center, float radius, float pitch, Vector2 viewport, bool immediate)
    {
        var bounds = ProjectionBounds(viewport);
        _sourceBasis = _basis; _sourceCenter = Center; _sourceDistance = Distance;
        _targetBasis = Matrix4x4.CreateRotationX(pitch);
        _targetCenter = center;
        _targetDistance = radius / Math.Min(bounds.Z,bounds.W);
        Transition = 0;
        if (immediate) Finish();
    }

    /// <summary>Next Modern UI find: glides to a new centre, keeping the zoom and the view angle.</summary>
    public void Glide(Vector3 center)
    {
        _sourceBasis = _targetBasis = _basis; _sourceCenter = Center; _sourceDistance = _targetDistance = Distance;
        _targetCenter = center; Transition = 0;
    }

    public void Advance(uint elapsedMilliseconds)
    {
        if (Transition < 0) return;
        float scaled = Transition * 10;
        int index = (int)Math.Clamp(scaled,0,9);
        float speed = (scaled-index)*(TransitionSpeed[index+1]-TransitionSpeed[index])+TransitionSpeed[index];
        Transition += speed * elapsedMilliseconds * .001f;
        if (Transition >= 1) { Finish(); return; }
        Center = Vector3.Lerp(_sourceCenter,_targetCenter,Transition);
        Distance = (_targetDistance-_sourceDistance)*Transition+_sourceDistance;
        var a = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(_sourceBasis);
        var b = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(_targetBasis);
        // 008C33B0 calls 008D23C0 (direct trigonometry, not the table sampler).
        _basis = EnbRetailCameraQuaternionBasis.Create(EnbRetailDirectQuaternionInterpolation.Interpolate(a,b,Transition));
    }

    private void Finish()
    {
        Center = _targetCenter; Distance = _targetDistance; _basis = _targetBasis; Transition = -1;
    }

    public void Zoom(float amount,float galaxyRadius)
    {
        Transition = -1;
        Distance -= (1/Math.Max(1,Distance)+1)*amount*Distance;
        Distance = Distance < 2 ? 2 : Math.Min(Distance,galaxyRadius*3);
    }

    public void Pan(Vector2 previousMinusCurrent,Vector2 minimum,Vector2 maximum)
    {
        Transition = -1;
        var displacement = Vector3.TransformNormal(new(previousMinusCurrent.X*Distance,-previousMinusCurrent.Y*Distance,0),_basis);
        var center = Center+displacement;
        // Clamp each axis to its corresponding galaxy bound. Retail's X upper
        // clamp reads Y's upper bound; that implementation accident is not retained.
        Center = new(Math.Clamp(center.X,minimum.X,maximum.X),Math.Clamp(center.Y,minimum.Y,maximum.Y),0);
    }

    public void Rotate(Vector2 previousMinusCurrent)
    {
        Transition = -1;
        const float degreesToRadians = 0.017453292f; // 00AFDF14
        _basis *= Matrix4x4.CreateRotationZ(previousMinusCurrent.X*50*degreesToRadians);
        _basis = Matrix4x4.CreateRotationX(previousMinusCurrent.Y*50*degreesToRadians)*_basis;
        float x = MathF.Atan2(_basis.M23,_basis.M22);
        _basis = Matrix4x4.CreateRotationX(Math.Clamp(x,0,1)-x)*_basis;
        float y = MathF.Atan2(_basis.M31,_basis.M33);
        _basis = Matrix4x4.CreateRotationY(Math.Clamp(y,-1,1)-y)*_basis;
        float z = MathF.Atan2(_basis.M12,_basis.M11);
        _basis = Matrix4x4.CreateRotationZ(Math.Clamp(z,-1,1)-z)*_basis;
    }

    public EnbResolvedRenderCamera Resolve(Vector2 authoredViewport,EnbRenderViewport pixels)
    {
        var bounds = ProjectionBounds(authoredViewport);
        var clip = new EnbRenderClipRange(.01f,1000);
        var projection = new EnbPerspectiveProjection(2*Math.Atan(bounds.Z),2*Math.Atan(bounds.W),
            bounds.Z/bounds.W,clip,EnbRetailProjectionMatrix.ComputeBackend(bounds,clip,false))
            { StoredProjectionBounds = bounds };
        var right = new Vector3(_basis.M11,_basis.M12,_basis.M13);
        var up = new Vector3(_basis.M21,_basis.M22,_basis.M23);
        var back = new Vector3(_basis.M31,_basis.M32,_basis.M33);
        var canonical = new Matrix4x4(-back.X,-back.Y,-back.Z,0,-right.X,-right.Y,-right.Z,0,up.X,up.Y,up.Z,0,0,0,0,1);
        return new(Center+back*Distance,Quaternion.CreateFromRotationMatrix(canonical),new(-back,right,up),pixels,projection);
    }

    public Vector3 SurfacePoint(Vector2 normalized,Vector2 viewport)
    {
        // 00944C40 then 007A72C0: unproject the unit-depth plane and intersect Z=0.
        var bounds = ProjectionBounds(viewport);
        var local = new Vector3(bounds.X+normalized.X*(bounds.Z-bounds.X),bounds.W-normalized.Y*(bounds.W-bounds.Y),-1);
        var ray = Vector3.TransformNormal(local,_basis);
        var origin = Center+new Vector3(_basis.M31,_basis.M32,_basis.M33)*Distance;
        return Math.Abs(ray.Z) < (double).0001f ? origin : origin+ray*(-origin.Z/ray.Z);
    }
}
