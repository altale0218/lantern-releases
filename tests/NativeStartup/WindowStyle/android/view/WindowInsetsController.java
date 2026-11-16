package android.view;
public final class WindowInsetsController {
    public static final int APPEARANCE_LIGHT_STATUS_BARS=8,APPEARANCE_LIGHT_NAVIGATION_BARS=16;
    public int appearance=-1;
    public void setSystemBarsAppearance(int value,int mask){appearance=value;}
}
