package com.lantern.transfer;

import android.os.Build;
import android.graphics.Color;
import android.util.Log;
import android.view.View;
import android.view.Window;
import android.view.WindowInsetsController;

/** Cosmetic startup styling must never prevent launching the managed application. */
public final class StartupWindowStyle {
    private StartupWindowStyle() {}
    public static void install(Window window,boolean dark) {
        if(window==null)return;
        try {
            // Install decor before any controller access. PhoneWindow may dereference mDecor internally.
            View decor=window.getDecorView();
            if(decor==null)return;
            window.setStatusBarColor(Color.TRANSPARENT);
            window.setNavigationBarColor(Color.TRANSPARENT);
            if(Build.VERSION.SDK_INT>=29){window.setNavigationBarContrastEnforced(false);window.setStatusBarContrastEnforced(false);}
            if(Build.VERSION.SDK_INT>=30)window.setDecorFitsSystemWindows(false);
            else decor.setSystemUiVisibility(decor.getSystemUiVisibility()|View.SYSTEM_UI_FLAG_LAYOUT_STABLE|View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN|View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
            if(decor.isAttachedToWindow())applyAppearance(window,decor,dark);
            else decor.addOnAttachStateChangeListener(new View.OnAttachStateChangeListener(){
                @Override public void onViewAttachedToWindow(View view){view.removeOnAttachStateChangeListener(this);applyAppearance(window,view,dark);}
                @Override public void onViewDetachedFromWindow(View view){}
            });
        }catch(RuntimeException error){Log.w("Lantern.Startup","Startup window styling skipped",error);}
    }
    private static void applyAppearance(Window window,View decor,boolean dark) {
        try {
            if(Build.VERSION.SDK_INT>=30){
                WindowInsetsController controller=window.getInsetsController();
                if(controller!=null){int light=WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;controller.setSystemBarsAppearance(dark?0:light,light);}
            }else{
                int light=View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
                decor.setSystemUiVisibility(dark?decor.getSystemUiVisibility()&~light:decor.getSystemUiVisibility()|light);
            }
        }catch(RuntimeException error){Log.w("Lantern.Startup","Startup system bar appearance skipped",error);}
    }
}
