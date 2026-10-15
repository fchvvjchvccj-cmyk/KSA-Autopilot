using System;

namespace KsaAutopilot
{
    public struct Vec
    {
        public double X, Y, Z;
        public Vec(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double Length => Math.Sqrt(Dot(this, this));
        public Vec Unit => Length > 1e-10 ? this / Length : new Vec();
        public static double Dot(Vec a, Vec b) => a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        public static Vec Cross(Vec a, Vec b) => new Vec(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
        public static Vec operator +(Vec a, Vec b) => new Vec(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static Vec operator -(Vec a, Vec b) => new Vec(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Vec operator *(Vec a, double b) => new Vec(a.X*b,a.Y*b,a.Z*b);
        public static Vec operator /(Vec a, double b) => new Vec(a.X/b,a.Y/b,a.Z/b);
        public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    }

    public enum Phase { Idle, Ascent, Coast, Circularize, Complete, Aborted }
    public sealed class Guidance
    {
        public Phase Phase { get; private set; }
        public double TargetAltitude = 200000, HeadingDegrees = 90, TurnStart = 1500, TurnEnd = 65000;
        public double Apoapsis, Periapsis, Altitude, VerticalSpeed, PitchDegrees, Throttle, DeltaV;
        public Vec Direction;
        public string Message = "待命";
        private double initialAltitude, elapsed, noThrustTime;
        private bool initialized;
        public bool Active => Phase == Phase.Ascent || Phase == Phase.Coast || Phase == Phase.Circularize;
        public void Start()
        {
            if (!double.IsFinite(TargetAltitude) || TargetAltitude < 1000 || TargetAltitude > 1e9 ||
                !double.IsFinite(HeadingDegrees) || !double.IsFinite(TurnStart) || !double.IsFinite(TurnEnd) ||
                TurnStart < 50 || TurnEnd < TurnStart+1000 || TurnEnd > TargetAltitude)
                throw new ArgumentException("检查目标高度和转弯高度：结束高度必须大于起始高度，并低于目标轨道。");
            Phase = Phase.Ascent; Throttle = 0; elapsed = noThrustTime = 0; initialized = false; Message = "起飞";
        }
        public void Abort(string reason) { Phase = Phase.Aborted; Throttle = 0; Message = reason; }
        public static double Clamp(double x, double lo, double hi) => Math.Max(lo, Math.Min(hi, x));
        public static void Elements(Vec p, Vec v, double mu, double radius, out double ap, out double pe)
        {
            double r = p.Length, energy = Vec.Dot(v,v)/2-mu/r;
            Vec h = Vec.Cross(p,v);
            double e = Math.Sqrt(Math.Max(0,1+2*energy*Vec.Dot(h,h)/(mu*mu)));
            double l = Vec.Dot(h,h)/mu;
            pe = l/(1+e)-radius;
            ap = energy >= 0 ? double.PositiveInfinity : l/(1-e)-radius;
            // 零角动量的直上直下轨迹用能量计算远地点，避免 0/0。
            if (energy < 0 && (e > 0.999999 || !double.IsFinite(ap))) ap = -mu/energy-radius;
        }
        public void Step(Vec p, Vec v, double mu, double radius, double acceleration, double dt)
        {
            if (!Active) return;
            if (!p.Finite || !v.Finite || !double.IsFinite(mu) || mu <= 0 || !double.IsFinite(radius) || radius <= 0 ||
                p.Length < radius*0.5 || !double.IsFinite(acceleration) || !double.IsFinite(dt) || dt < 0 || dt > 2)
            { Abort("导航数据无效或物理步长过大，已中止"); return; }
            if (!initialized) { initialAltitude = p.Length-radius; initialized = true; }
            elapsed += dt;
            double r = p.Length;
            Vec up = p/r;
            Vec east = Vec.Cross(new Vec(0,0,1),up).Unit;
            if (east.Length < 0.5) east = Vec.Cross(new Vec(0,1,0),up).Unit;
            Vec north = Vec.Cross(up,east).Unit;
            double az = HeadingDegrees*Math.PI/180;
            Vec launchHorizontal = north*Math.Cos(az)+east*Math.Sin(az);
            Altitude = r-radius;
            VerticalSpeed = Vec.Dot(v,up);
            Vec horizontalVelocity = v-up*VerticalSpeed;
            double horizontalSpeed = horizontalVelocity.Length;
            Vec horizontal = Phase == Phase.Ascent ? launchHorizontal : horizontalVelocity.Unit;
            if (horizontal.Length < 0.5) horizontal = launchHorizontal;
            Elements(p,v,mu,radius,out double ap,out double pe); Apoapsis = ap; Periapsis = pe;
            if (!double.IsFinite(pe) || double.IsNaN(ap)) { Abort("轨道数据无效，已中止"); return; }
            if (Altitude < initialAltitude-150 && VerticalSpeed < -10) { Abort("飞行器正在坠落，已中止"); return; }
            if (elapsed > 3600) { Abort("入轨超时，已中止"); return; }
            if (Phase == Phase.Ascent)
            {
                double h = Altitude-initialAltitude;
                double progress = Clamp((h-TurnStart)/(TurnEnd-TurnStart),0,1);
                PitchDegrees = 90*(1-Math.Pow(progress,0.72));
                if (h > TurnStart && horizontalSpeed > 100)
                {
                    double flightPath = Math.Atan2(VerticalSpeed,horizontalSpeed)*180/Math.PI;
                    PitchDegrees = Math.Max(PitchDegrees, flightPath-8);
                }
                PitchDegrees = Clamp(PitchDegrees,0,90);
                double remaining = TargetAltitude-ap;
                Throttle = Clamp(remaining/Math.Max(1500,VerticalSpeed*4),0.05,1);
                if (remaining <= Math.Max(100,VerticalSpeed*0.15))
                { Phase = Phase.Coast; Throttle = 0; Message = "滑行至远地点"; }
                else Message = h < TurnStart ? "垂直起飞" : "重力转弯";
            }
            if (Phase == Phase.Coast)
            {
                PitchDegrees = 0; Throttle = 0;
                double circular = Math.Sqrt(mu/r);
                DeltaV = Math.Max(0,circular-horizontalSpeed);
                double burnTime = DeltaV/Math.Max(0.1,acceleration);
                double gravity = mu/(r*r)-horizontalSpeed*horizontalSpeed/r;
                double timeToAp = VerticalSpeed/Math.Max(0.05,gravity);
                if (timeToAp <= burnTime*0.55+3 || VerticalSpeed <= 0)
                { Phase = Phase.Circularize; Message = "圆轨道补燃"; }
            }
            if (Phase == Phase.Circularize)
            {
                double circular = Math.Sqrt(mu/r);
                DeltaV = Math.Max(0,circular-horizontalSpeed);
                // 径向速度闭环抵消重力与离心加速度差，长时间补燃也能保持高度。
                double desiredVr = Clamp((TargetAltitude-Altitude)*0.025,-60,60);
                double radialAccel = (desiredVr-VerticalSpeed)*0.12+mu/(r*r)-horizontalSpeed*horizontalSpeed/r;
                double tangentAccel = (circular-horizontalSpeed)*0.12;
                double requested = Math.Sqrt(radialAccel*radialAccel+tangentAccel*tangentAccel);
                Throttle = Clamp(requested/Math.Max(acceleration,0.1),0,1);
                PitchDegrees = Math.Atan2(radialAccel,tangentAccel)*180/Math.PI;
                Direction = (up*radialAccel+horizontal*tangentAccel).Unit;
                double tolerance = Math.Max(1500,TargetAltitude*0.02);
                if (pe >= TargetAltitude-tolerance && ap <= TargetAltitude+tolerance && Math.Abs(VerticalSpeed) < 15)
                { Phase = Phase.Complete; Throttle = 0; Message = "入轨完成"; }
                else if (horizontalSpeed > circular*1.4) { Abort("速度超出圆轨道控制范围，已关机"); }
            }
            double pitch = PitchDegrees*Math.PI/180;
            if (Phase != Phase.Circularize) Direction = (up*Math.Sin(pitch)+horizontal*Math.Cos(pitch)).Unit;
            if (Throttle > 0.05 && acceleration < 0.1) noThrustTime += dt; else noThrustTime = 0;
            if (noThrustTime > 5) Abort("没有可用推力：检查发动机、燃料和分级");
        }
    }
}
