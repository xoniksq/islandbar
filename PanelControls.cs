using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar;
static class PanelControls
{
    public static readonly Brush Surface=Solid(8,8,10);
    public static readonly Brush Ink=Solid(4,4,5);
    public static readonly Brush Outline=Solid(44,45,49);
    public static readonly Brush Secondary=Solid(168,170,178);
    public static readonly Brush Silver=Solid(221,224,232);
    public static readonly Brush Tile=Metal(28,12);
    public static readonly Brush Chrome=Metal(46,17);
    public static readonly Brush Shell=Metal(16,5);
    public static readonly Brush Dock=Metal(18,2);
    public static readonly Brush Rim=Freeze(new LinearGradientBrush(
        new GradientStopCollection {
            new(Color.FromRgb(143,146,155),0),
            new(Color.FromRgb(72,74,82),.24),
            new(Color.FromRgb(36,37,41),.57),
            new(Color.FromRgb(55,56,62),1)
        },new Point(.08,0),new Point(.93,1)));
    static readonly Brush Reflection=Freeze(new RadialGradientBrush(
        new GradientStopCollection {new(Color.FromArgb(27,213,219,235),0),new(Colors.Transparent,1)}) {
        Center=new Point(.23,-.10),GradientOrigin=new Point(.18,-.10),RadiusX=.85,RadiusY=.57
    });
    static readonly Brush TileRim=Freeze(new LinearGradientBrush(
        new GradientStopCollection {new(Color.FromRgb(68,69,76),0),new(Color.FromRgb(28,29,33),1)},90));

    static Brush Solid(byte r,byte g,byte b)=>Freeze(new SolidColorBrush(Color.FromRgb(r,g,b)));
    static Brush Metal(byte top,byte bottom)=>Freeze(new LinearGradientBrush(
        Color.FromRgb(top,top,(byte)(top+3)),Color.FromRgb(bottom,bottom,(byte)(bottom+2)),90));
    static T Freeze<T>(T value) where T:Freezable {value.Freeze();return value;}

    static readonly ControlTemplate ButtonTemplate=(ControlTemplate)XamlReader.Parse("""
    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
      <Grid Background="Transparent">
        <Border Background="{TemplateBinding Background}" CornerRadius="11" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"/>
        <Border x:Name="Feedback" CornerRadius="11" BorderThickness="1" BorderBrush="#80EBEEF5" Opacity="0" IsHitTestVisible="False">
          <Border.Background>
            <LinearGradientBrush StartPoint="0,0" EndPoint="1,1"><GradientStop Color="#36EFF3FF"/><GradientStop Color="#06FFFFFF" Offset="1"/></LinearGradientBrush>
          </Border.Background>
        </Border>
        <ContentPresenter x:Name="Body" Margin="{TemplateBinding Padding}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" RecognizesAccessKey="True">
          <ContentPresenter.RenderTransform><TranslateTransform/></ContentPresenter.RenderTransform>
        </ContentPresenter>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Feedback" Property="Opacity" Value="0.53"/></Trigger>
        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
    """);

    public static Button Button(object content,string name,double height=36,bool center=false)
    {
        var button=new Button {
            Content=content, Height=height, Padding=new Thickness(10,0,10,0),
            Background=Brushes.Transparent, Foreground=Brushes.White, Template=ButtonTemplate,
            BorderThickness=new Thickness(0),
            HorizontalContentAlignment=center?HorizontalAlignment.Center:HorizontalAlignment.Stretch,
            Cursor=Cursors.Hand, FocusVisualStyle=null
        };
        System.Windows.Automation.AutomationProperties.SetName(button,name);
        void Feedback(double opacity,double y,int milliseconds)
        {
            var time=TimeSpan.FromMilliseconds(ChromeMotion.Enabled?milliseconds:0);
            var ease=new CubicEase {EasingMode=EasingMode.EaseOut};
            if(button.Template.FindName("Feedback",button) is Border layer)
                layer.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(opacity,time){EasingFunction=ease});
            if(button.Template.FindName("Body",button) is ContentPresenter body&&body.RenderTransform is TranslateTransform shift){
                if(shift.IsFrozen){shift=shift.Clone();body.RenderTransform=shift;}
                shift.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(ChromeMotion.Enabled?y:0,time){EasingFunction=ease});
            }
        }
        button.MouseEnter+=(_,_)=>Feedback(.63,-1,155);
        button.MouseLeave+=(_,_)=>Feedback(button.IsKeyboardFocused?.53:0,0,190);
        button.PreviewMouseLeftButtonDown+=(_,_)=>Feedback(1,1,85);
        button.PreviewMouseLeftButtonUp+=(_,_)=>Feedback(button.IsMouseOver?.63:0,button.IsMouseOver?-1:0,175);
        button.GotKeyboardFocus+=(_,_)=>{if(!button.IsMouseOver)Feedback(.53,0,145);};
        button.LostKeyboardFocus+=(_,_)=>{if(!button.IsMouseOver)Feedback(0,0,145);};
        return button;
    }

    public static Border Frame(UIElement content,Thickness padding)
    {
        var layers=new Grid();
        layers.Children.Add(new Border {Background=Reflection,CornerRadius=new CornerRadius(23),IsHitTestVisible=false});
        layers.Children.Add(new Border {Child=content,Padding=padding});
        return new Border {
            Child=layers,CornerRadius=new CornerRadius(24),Background=Shell,
            BorderBrush=Rim,BorderThickness=new Thickness(1),UseLayoutRounding=true,SnapsToDevicePixels=true
        };
    }

    public static Border Well(UIElement child,Thickness padding,double radius=13)=>new() {
        Child=child,Padding=padding,CornerRadius=new CornerRadius(radius),Background=Ink,
        BorderBrush=Outline,BorderThickness=new Thickness(1)
    };

    public static void FinishTile(Button button)
    {
        button.Background=Tile;
        button.BorderBrush=TileRim;
        button.BorderThickness=new Thickness(1);
    }

    public static TextBlock Eyebrow(string text)=>new() {
        Text=text,FontSize=10,FontWeight=FontWeights.SemiBold,Foreground=Secondary,
        VerticalAlignment=VerticalAlignment.Center
    };

    public static Border Keycap(string key)=>new() {
        Child=new TextBlock {Text=key,FontSize=10,FontWeight=FontWeights.SemiBold,Foreground=Silver,
            VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center},
        Background=Tile,BorderBrush=TileRim,BorderThickness=new Thickness(1),
        CornerRadius=new CornerRadius(5),Padding=new Thickness(7,2,7,2),VerticalAlignment=VerticalAlignment.Center
    };

    static readonly ControlTemplate HintTemplate=(ControlTemplate)XamlReader.Parse("""
    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ToolTip">
      <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="8" Padding="{TemplateBinding Padding}">
        <ContentPresenter/>
      </Border>
    </ControlTemplate>
    """);
    public static ToolTip Hint(string text)=>new() {
        Content=text, Background=Shell, Foreground=Brushes.White, BorderBrush=Rim,
        Template=HintTemplate,Placement=PlacementMode.Top,VerticalOffset=-6,Padding=new Thickness(10,6,10,6)
    };

    public static readonly ControlTemplate Slider=(ControlTemplate)XamlReader.Parse("""
    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
      <Grid Height="24" Background="Transparent">
        <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}">
          <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False"><RepeatButton.Template><ControlTemplate><Border Height="4" Background="#CFD2DA" CornerRadius="2"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
          <Track.Thumb><Thumb Width="14" Height="20"><Thumb.Template><ControlTemplate><Ellipse Width="13" Height="13" Stroke="#84868E" StrokeThickness="1"><Ellipse.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="0,1"><GradientStop Color="#FFFFFF"/><GradientStop Color="#A5A8B0" Offset="1"/></LinearGradientBrush></Ellipse.Fill></Ellipse></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
          <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False"><RepeatButton.Template><ControlTemplate><Border Height="4" Background="#303136" CornerRadius="2"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
        </Track>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
    """);
    public static readonly ControlTemplate CheckBox=(ControlTemplate)XamlReader.Parse("""
    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
      <DockPanel Background="Transparent">
        <Border x:Name="Box" DockPanel.Dock="Left" Width="15" Height="15" Margin="0,0,10,0" CornerRadius="4" Background="#16171B" BorderBrush="#5E6068" BorderThickness="1">
          <Path x:Name="Tick" Data="M 2 6 L 5 9 L 11 3" Stroke="#101114" StrokeThickness="1.7" StrokeLineJoin="Round" Visibility="Collapsed"/>
        </Border>
        <ContentPresenter VerticalAlignment="Center"/>
      </DockPanel>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#ADB0BA"/></Trigger>
        <Trigger Property="IsChecked" Value="True"><Setter TargetName="Box" Property="Background" Value="#D3D6DF"/><Setter TargetName="Box" Property="BorderBrush" Value="#D3D6DF"/><Setter TargetName="Tick" Property="Visibility" Value="Visible"/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
    """);
    public static readonly Style ScrollBar=(Style)XamlReader.Parse("""
    <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
      <Setter Property="Width" Value="12"/><Setter Property="Background" Value="Transparent"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
        <Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}">
          <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageUpCommand}" Focusable="False"><RepeatButton.Template><ControlTemplate><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
          <Track.Thumb><Thumb MinHeight="24"><Thumb.Template><ControlTemplate><Border Background="#676A74" Width="4" CornerRadius="2"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
          <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageDownCommand}" Focusable="False"><RepeatButton.Template><ControlTemplate><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
        </Track>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    """);
    public static ScrollViewer Scroller(UIElement child,double maxHeight)
    {
        var viewer=new ScrollViewer {
            Content=child, MaxHeight=maxHeight, VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, HorizontalContentAlignment=HorizontalAlignment.Stretch,
            UseLayoutRounding=true
        };
        viewer.Resources[typeof(ScrollBar)]=ScrollBar;
        return viewer;
    }
}
