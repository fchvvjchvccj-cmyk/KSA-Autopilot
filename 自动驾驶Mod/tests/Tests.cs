using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using KsaAutopilot;
using Brutal.Numerics;

public static class Tests
{
    private static int checks;
    private static void Assert(bool ok,string name) { if (!ok) throw new Exception(name); checks++; }
    private static void Near(double actual,double expected,double tolerance,string name) => Assert(Math.Abs(actual-expected)<=tolerance,name+": "+actual);
    public static int Main()
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            double mu = 3.986004418e14, radius = 6371000, r = radius+200000;
            Guidance.Elements(new Vec(r,0,0),new Vec(0,Math.Sqrt(mu/r),0),mu,radius,out double ap,out double pe);
            Near(ap,200000,1,"圆轨道远地点"); Near(pe,200000,1,"圆轨道近地点");
            Guidance.Elements(new Vec(radius,0,0),new Vec(1000,0,0),mu,radius,out ap,out pe);
            Assert(double.IsFinite(ap) && ap>0,"径向发射轨道");
            var invalid = new Guidance { TurnEnd = 300000 };
            bool rejected=false; try { invalid.Start(); } catch(ArgumentException) { rejected=true; }
            Assert(rejected,"非法设置拒绝");
            var p = new Guidance(); p.Start(); p.Step(new Vec(double.NaN,0,0),new Vec(),mu,radius,20,0.1);
            Assert(p.Phase==Phase.Aborted && p.Throttle==0,"非法导航关机");
            p = new Guidance(); p.Start(); p.Step(new Vec(radius,0,0),new Vec(0,460,0),mu,radius,20,3);
            Assert(p.Phase==Phase.Aborted,"大步长中止");
            p = new Guidance(); p.Start(); p.Abort("测试");
            p.Step(new Vec(radius,0,0),new Vec(),mu,radius,20,0.1);
            Assert(!p.Active && p.Throttle==0,"中止不重启");
            p = new Guidance(); p.Start();
            for(int i=0;i<70;i++) p.Step(new Vec(radius,0,0),new Vec(),mu,radius,0,0.1);
            Assert(p.Phase==Phase.Aborted,"无推力超时");
            p = new Guidance(); p.Start(); p.Step(new Vec(0,0,radius),new Vec(),mu,radius,20,0.1);
            Assert(p.Direction.Finite && p.Direction.Length>0.99,"极点方向有限");
            var fc = new KSA.FlightComputer();
            // 只编译方法，不执行游戏入口，也不建立任何窗口或渲染上下文。
            foreach(var method in typeof(Autopilot).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static))
            {
                if (method.ContainsGenericParameters) continue;
                RuntimeHelpers.PrepareMethod(method.MethodHandle); checks++;
            }
            KSA.FlightComputerNavigation nav = default; var input = new KSA.ManualControlInputs();
            input.EngineOn=true; input.EngineThrottle=0.42f;
            Autopilot.Control(fc,ref nav,ref input);
            Assert(input.EngineOn && Math.Abs(input.EngineThrottle-0.42)<1e-6,"未接管飞行器输入保持");
            Integration(fc);
            // 静态绑定检查同时验证修改后的 KSA.dll 能在真实 .NET 10 运行库中加载与即时编译。
            Assembly game=typeof(KSA.FlightComputer).Assembly;
            foreach(string type in new[]{"KSA.Vehicle","KSA.VehicleUpdateState","KSA.FlightComputer","KSA.Universe"})
            foreach(var method in game.GetType(type).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static))
            {
                if (method.Name=="PrepareWorker" || method.Name=="PrepareFromVehicle" || method.Name=="ComputeControl" ||
                    method.Name=="UpdateAttitudeTarget" || method.Name=="ProcessInput" || (type=="KSA.Universe" && method.Name=="OnDrawUi"))
                { RuntimeHelpers.PrepareMethod(method.MethodHandle); checks++; }
            }
            Simulate("地球200km",mu,radius,200000,1.65,4500,0.1);
            Simulate("地球120km",mu,radius,120000,1.4,4500,0.1);
            Simulate("月球50km",4.9048695e12,1737400,50000,2.0,3200,0.1);
            Console.WriteLine($"PASS: {checks} checks"); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Integration(KSA.FlightComputer fc)
    {
        Type api=typeof(Autopilot);
        var flags=BindingFlags.NonPublic|BindingFlags.Static;
        var pilot=(Guidance)api.GetField("Pilot",flags).GetValue(null);
        var vehicle=(KSA.Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(KSA.Vehicle));
        var state=(KSA.VehicleUpdateState)RuntimeHelpers.GetUninitializedObject(typeof(KSA.VehicleUpdateState));
        var data=(KSA.VehicleUpdateData)RuntimeHelpers.GetUninitializedObject(typeof(KSA.VehicleUpdateData));
        typeof(KSA.VehicleUpdateState).GetField("ReadOnlyVehicle").SetValue(state,vehicle);
        typeof(KSA.VehicleUpdateData).GetField("NewFlightComputer").SetValue(data,fc);
        typeof(KSA.VehicleUpdateState).GetField("UpdateData").SetValue(state,data);
        api.GetField("owner",flags).SetValue(null,vehicle); pilot.Start();
        Autopilot.Bind(state);
        KSA.FlightComputerNavigation nav=default;
        nav.PositionCci=new double3(6371000,0,0);
        foreach(var direction in new[]{new double3(1,0,0),new double3(0,1,0),new double3(0,0,1),new double3(-1,0,0)})
        {
            api.GetField("desired",flags).SetValue(null,direction);
            api.GetField("hasDirection",flags).SetValue(null,true);
            Autopilot.Attitude(fc,ref nav);
            double3 actual=KSA.Double3Ex.Transform(double3.UnitX,fc.AttitudeTarget.Target2Cci);
            Assert(double3.Dot(actual,direction)>0.999999,"游戏 +X 推力轴姿态映射");
        }
        KSA.FlightComputerOutput output=default; output.NextWakeupDeltaTime=double.PositiveInfinity;
        Autopilot.AfterControl(fc,ref output);
        Near(output.NextWakeupDeltaTime,0.1,1e-9,"滑行唤醒间隔");
        // 线程副本必须重新绑定到同一飞行器，异常后必须关机。
        var copy=new KSA.FlightComputer(fc);
        typeof(KSA.VehicleUpdateData).GetField("NewFlightComputer").SetValue(data,copy);
        Autopilot.Bind(state);
        var input=new KSA.ManualControlInputs {EngineOn=true,EngineThrottle=1};
        Autopilot.Control(copy,ref nav,ref input);
        Assert(!input.EngineOn && input.EngineThrottle==0 && !pilot.Active,"线程副本导航异常关机");
        api.GetField("owner",flags).SetValue(null,null);
    }
    private static void Simulate(string name,double mu,double radius,double target,double twr,double exhaust,double dt)
    {
        double mass=40000,dry=2500,thrust=twr*mass*mu/(radius*radius);
        var pilot=new Guidance {TargetAltitude=target,TurnStart=Math.Min(1500,target*0.02),TurnEnd=target*0.325}; pilot.Start();
        Vec pos=new Vec(radius+1,0,0),vel=new Vec(0,radius>2000000 ? 465 : 0,0);
        Phase last=Phase.Idle;
        for(int i=0;i<36000 && pilot.Active;i++)
        {
            double accel=mass>dry ? thrust/mass : 0;
            pilot.Step(pos,vel,mu,radius,accel,dt);
            if(last!=pilot.Phase) { Console.WriteLine($"{name}: t={i*dt:F1} {pilot.Phase} h={pilot.Altitude:F0} ap={pilot.Apoapsis:F0} pe={pilot.Periapsis:F0} mass={mass:F0}"); last=pilot.Phase; }
            if (!double.IsFinite(pilot.Throttle) || pilot.Throttle<0 || pilot.Throttle>1) throw new Exception("油门越界");
            Vec thrustAccel=pilot.Direction*(accel*pilot.Throttle);
            // 中点积分模拟中心引力与有限推力，不假装验证游戏内大气和部件物理。
            Vec gravity=pos*(-mu/Math.Pow(pos.Length,3));
            Vec midPos=pos+vel*(dt/2),midVel=vel+(gravity+thrustAccel)*(dt/2);
            Vec midGravity=midPos*(-mu/Math.Pow(midPos.Length,3));
            pos=pos+midVel*dt; vel=vel+(midGravity+thrustAccel)*dt;
            mass=Math.Max(dry,mass-thrust*pilot.Throttle*dt/exhaust);
            if(pos.Length<radius-1000) { pilot.Abort("模拟撞地"); break; }
        }
        Console.WriteLine($"{name}: {pilot.Message}, ap={pilot.Apoapsis:F0}, pe={pilot.Periapsis:F0}");
        Assert(pilot.Phase==Phase.Complete,name+"未能入轨");
        Assert(pilot.Throttle==0,name+"完成后关机");
        checks++;
    }
}
