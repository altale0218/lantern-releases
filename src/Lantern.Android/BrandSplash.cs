using Android.Content;
using Android.Graphics;
using Lantern.AppLogic;
namespace Lantern.AndroidApp;

internal sealed class BrandSplash(Context context,bool dark) : Android.Views.View(context)
{
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float d=Resources?.DisplayMetrics?.Density??1,x=Width/2f,y=Height/2f-36*d;
        canvas.DrawColor(dark?Color.Rgb(13,16,22):Color.Rgb(245,247,250));
        using var paint=new Paint(PaintFlags.AntiAlias){Color=Color.Rgb(167,216,75)};
        canvas.DrawRoundRect(x-32*d,y-32*d,x+32*d,y+32*d,22*d,22*d,paint);
        paint.Color=Color.Rgb(32,41,54);paint.StrokeWidth=3*d;paint.SetStyle(Paint.Style.Stroke!);
        using var path=new Android.Graphics.Path();path.MoveTo(x+4*d,y-17*d);path.LineTo(x-11*d,y+3*d);path.LineTo(x,y+3*d);path.LineTo(x-4*d,y+18*d);path.LineTo(x+12*d,y-4*d);path.LineTo(x+2*d,y-4*d);path.Close();canvas.DrawPath(path,paint);
        paint.SetStyle(Paint.Style.Fill!);paint.TextAlign=Paint.Align.Center;
        paint.Color=dark?Color.Rgb(237,241,247):Color.Rgb(32,41,54);paint.TextSize=24*d;paint.SetTypeface(Typeface.Create("sans-serif",TypefaceStyle.Bold));
        canvas.DrawText(AppBrand.Name,x,y+66*d,paint);
        paint.SetTypeface(Typeface.Default);paint.TextSize=14*d;
        paint.Color=dark?Color.Rgb(159,171,187):Color.Rgb(85,101,121);
        canvas.DrawText(AppBrand.Motto,x,y+99*d,paint);
    }
}
