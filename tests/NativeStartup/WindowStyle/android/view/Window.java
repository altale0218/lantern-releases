package android.view;
public final class Window {
    private View decor;
    public int controllerReads;
    public boolean nullController,failController,failStyle;
    public final WindowInsetsController controller=new WindowInsetsController();
    public View getDecorView(){if(decor==null)decor=new View();return decor;}
    public WindowInsetsController getInsetsController(){
        // AOSP PhoneWindow dereferences its decor here. A not-yet-attached OEM may return null.
        if(decor==null)throw new NullPointerException("mDecor is null");
        controllerReads++;
        if(!decor.isAttachedToWindow())throw new IllegalStateException("Controller accessed before attachment");
        if(failController)throw new IllegalStateException("OEM controller unavailable");
        return nullController?null:controller;
    }
    public void setStatusBarColor(int value){if(failStyle)throw new IllegalStateException("OEM styling unavailable");}
    public void setNavigationBarColor(int value){}
    public void setNavigationBarContrastEnforced(boolean value){}
    public void setStatusBarContrastEnforced(boolean value){}
    public void setDecorFitsSystemWindows(boolean value){}
}
