using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar;

static class ChromeMotion
{
    public static bool PreferencesEnabled {get;set;}=true;
    public static bool Enabled=>PreferencesEnabled&&SystemParameters.ClientAreaAnimation;

    public static void Enter(FrameworkElement element,bool anchored=true)
    {
        if(!Enabled)return;
        var scale=new ScaleTransform(1,1);
        var lift=new TranslateTransform();
        element.RenderTransformOrigin=anchored?new Point(.5,1):new Point(.5,.5);
        element.RenderTransform=new TransformGroup {Children=new TransformCollection {scale,lift}};
        var ease=new CubicEase {EasingMode=EasingMode.EaseOut};
        var time=TimeSpan.FromMilliseconds(255);
        element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(178)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,new DoubleAnimation(.966,1,time){EasingFunction=ease});
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,new DoubleAnimation(.95,1,time){EasingFunction=ease});
        lift.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(anchored?8:5,0,time){EasingFunction=ease});
    }

    public static void Exit(FrameworkElement element,Action complete)
    {
        if(!Enabled){complete();return;}
        var scale=new ScaleTransform(1,1);
        var lift=new TranslateTransform();
        var current=element.RenderTransform.Value;
        scale.ScaleX=current.M11;scale.ScaleY=current.M22;lift.Y=current.OffsetY;
        element.RenderTransform=new TransformGroup {Children=new TransformCollection {scale,lift}};
        var time=TimeSpan.FromMilliseconds(122);
        var ease=new QuadraticEase {EasingMode=EasingMode.EaseIn};
        var fade=new DoubleAnimation(element.Opacity,0,time){EasingFunction=ease};
        fade.Completed+=(_,_)=>complete();
        element.BeginAnimation(UIElement.OpacityProperty,fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,new DoubleAnimation(.978,time){EasingFunction=ease});
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,new DoubleAnimation(.973,time){EasingFunction=ease});
        lift.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(7,time){EasingFunction=ease});
    }

    public static void Reveal(FrameworkElement element,int step=0)
    {
        if(!Enabled)return;
        var shift=new TranslateTransform();
        element.RenderTransform=shift;
        element.Opacity=0;
        var delay=TimeSpan.FromMilliseconds(26+Math.Min(step,5)*22);
        var time=TimeSpan.FromMilliseconds(224);
        var ease=new CubicEase {EasingMode=EasingMode.EaseOut};
        element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,time){BeginTime=delay,EasingFunction=ease});
        shift.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(7,0,time){BeginTime=delay,EasingFunction=ease});
    }
}
