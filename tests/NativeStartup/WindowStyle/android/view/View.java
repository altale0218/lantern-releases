package android.view;
import java.util.*;
public final class View {
    public static final int SYSTEM_UI_FLAG_LAYOUT_STABLE=256,SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN=1024,SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION=512,SYSTEM_UI_FLAG_LIGHT_STATUS_BAR=8192,SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR=16;
    public interface OnAttachStateChangeListener {void onViewAttachedToWindow(View view);void onViewDetachedFromWindow(View view);}
    public final List<OnAttachStateChangeListener> listeners=new ArrayList<>();
    private boolean attached;private int flags;
    public boolean isAttachedToWindow(){return attached;}
    public void addOnAttachStateChangeListener(OnAttachStateChangeListener value){listeners.add(value);}
    public void removeOnAttachStateChangeListener(OnAttachStateChangeListener value){listeners.remove(value);}
    public int getSystemUiVisibility(){return flags;}
    public void setSystemUiVisibility(int value){flags=value;}
    public void attach(){attached=true;for(OnAttachStateChangeListener listener:new ArrayList<>(listeners))listener.onViewAttachedToWindow(this);}
    public void detach(){attached=false;for(OnAttachStateChangeListener listener:new ArrayList<>(listeners))listener.onViewDetachedFromWindow(this);}
}
