import android.os.Build;
import android.view.View;
import android.view.Window;
import com.lantern.transfer.StartupWindowStyle;

/** JVM contract regression. These doubles model pre-decor and pre-attachment Android window states. */
public final class StartupWindowStyleTest {
    private static int checks;
    private static void check(boolean value,String label){if(!value)throw new AssertionError(label);checks++;}
    public static void main(String[] args) {
        Build.VERSION.SDK_INT=37;
        boolean reproduced=false;
        try {Window old=new Window();old.setDecorFitsSystemWindows(false);old.getInsetsController().setSystemBarsAppearance(24,24);}
        catch(NullPointerException expected){reproduced=true;}
        check(reproduced,"0.6.2 pre-decor controller access reproduces the startup failure");
        for(int api:new int[]{26,29,30,36,37})for(boolean dark:new boolean[]{false,true}) {
            Build.VERSION.SDK_INT=api;Window window=new Window();
            StartupWindowStyle.install(window,dark);
            check(window.controllerReads==0,"No premature controller read API "+api);
            View decor=window.getDecorView();decor.attach();
            if(api>=30) {
                check(window.controllerReads==1,"Apply on window attachment API "+api);
                check(window.controller.appearance==(dark?0:24),"Correct dark/light appearance API "+api);
            }else check((decor.getSystemUiVisibility()&8208)==(dark?0:8208),"Legacy light bar flags API "+api);
            check(decor.listeners.isEmpty(),"Attachment listener removed API "+api);
            decor.detach();decor.attach();
            check(window.controllerReads==(api>=30?1:0),"No stale callbacks after reattachment API "+api);
        }
        Build.VERSION.SDK_INT=37;
        Window attached=new Window();attached.getDecorView().attach();StartupWindowStyle.install(attached,false);
        check(attached.controllerReads==1&&attached.controller.appearance==24,"Already-attached window handled");
        Window missing=new Window();missing.nullController=true;StartupWindowStyle.install(missing,false);missing.getDecorView().attach();
        check(missing.controllerReads==1,"Null controller is harmless");
        Window denied=new Window();denied.failController=true;StartupWindowStyle.install(denied,false);denied.getDecorView().attach();
        check(denied.controllerReads==1,"OEM controller exception does not escape callback");
        Window styleError=new Window();styleError.failStyle=true;StartupWindowStyle.install(styleError,false);
        check(styleError.controllerReads==0,"Cosmetic setup failure does not prevent launch");
        StartupWindowStyle.install(null,false);check(true,"Missing window does not crash");
        System.out.println("PASS "+checks+" startup window lifecycle contract checks; old pre-decor failure reproduced; Android APIs 26/29/30/36/37 modeled (not device tests)");
    }
}
