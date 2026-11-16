package com.lantern.transfer;
import android.content.Context;
import android.graphics.*;
import android.view.View;

/** Framework-only launch surface. Keep copy and geometry aligned with the managed launch surface. */
public final class BrandSplashView extends View {
    private final boolean dark;
    public BrandSplashView(Context context,boolean dark){super(context);this.dark=dark;}
    @Override protected void onDraw(Canvas canvas){
        super.onDraw(canvas);
        float d=getResources().getDisplayMetrics().density,x=getWidth()/2f,y=getHeight()/2f-36*d;
        canvas.drawColor(dark?Color.rgb(13,16,22):Color.rgb(245,247,250));
        Paint p=new Paint(Paint.ANTI_ALIAS_FLAG);p.setColor(Color.rgb(167,216,75));canvas.drawRoundRect(x-32*d,y-32*d,x+32*d,y+32*d,22*d,22*d,p);
        p.setColor(Color.rgb(32,41,54));p.setStrokeWidth(3*d);p.setStyle(Paint.Style.STROKE);
        Path path=new Path();path.moveTo(x+4*d,y-17*d);path.lineTo(x-11*d,y+3*d);path.lineTo(x,y+3*d);path.lineTo(x-4*d,y+18*d);path.lineTo(x+12*d,y-4*d);path.lineTo(x+2*d,y-4*d);path.close();canvas.drawPath(path,p);
        p.setStyle(Paint.Style.FILL);p.setTextAlign(Paint.Align.CENTER);p.setColor(dark?Color.rgb(237,241,247):Color.rgb(32,41,54));p.setTextSize(24*d);p.setTypeface(Typeface.create("sans-serif",Typeface.BOLD));canvas.drawText("闪传 Lantern",x,y+66*d,p);
        p.setTypeface(Typeface.DEFAULT);p.setTextSize(14*d);p.setColor(dark?Color.rgb(159,171,187):Color.rgb(85,101,121));
        canvas.drawText("同一网络，文件轻松互传",x,y+99*d,p);
    }
}
