package com.lantern.transfer;

import android.app.Application;
import android.content.Context;
import android.os.Build;
import java.io.*;
import java.nio.charset.StandardCharsets;

/** No managed types: attachBaseContext runs before ContentProvider / Mono initialization. */
public final class CrashCaptureApplication extends Application {
    private String process;
    @Override protected void attachBaseContext(Context base) {
        super.attachBaseContext(base);
        process=Build.VERSION.SDK_INT>=28?Application.getProcessName():base.getPackageName();
        installJournal();
        if(!process.endsWith(":startup"))stage("原生 Application.attachBaseContext 完成；准备初始化 Provider / .NET runtime");
    }
    @Override public void onCreate() {
        super.onCreate();
        // Preserve capture if the runtime installed its own handler while providers initialized.
        installJournal();
        if(!process.endsWith(":startup"))stage("原生 Application.onCreate 完成；准备创建 MainActivity");
    }
    private void installJournal() {
        try {
            Thread.UncaughtExceptionHandler previous=Thread.getDefaultUncaughtExceptionHandler();
            if(previous instanceof CrashJournal)return;
            File report=new File(getFilesDir(),process.endsWith(":startup")?"startup-java-crash.txt":"java-crash.txt");
            Thread.setDefaultUncaughtExceptionHandler(new CrashJournal(report,()-> {
                String phase=process.endsWith(":startup")?"原生启动进程，尚未加载 .NET":"未知";
                try(FileInputStream input=new FileInputStream(new File(getFilesDir(),process.endsWith(":startup")?"startup-launch-stage.txt":"startup-stage.txt"))) {
                    byte[] bytes=new byte[4096];int count=input.read(bytes);if(count>0)phase=new String(bytes,0,count,StandardCharsets.UTF_8);
                } catch(Exception ignored) {}
                return "Lantern Android 0.7.14\n来源：原生 Java 未捕获异常\n设备："+Build.MANUFACTURER+" "+Build.MODEL+
                    "\nAndroid："+Build.VERSION.RELEASE+" / API "+Build.VERSION.SDK_INT+
                    "\n时间："+new java.util.Date()+"\n进程："+process+"\n阶段："+phase;
            },previous));
        } catch(Exception ignored) {}
    }
    private void stage(String value) {
        try(FileOutputStream output=new FileOutputStream(new File(getFilesDir(),"startup-stage.txt"))) {
            output.write(value.getBytes(StandardCharsets.UTF_8));
        } catch(Exception ignored) {}
    }
}
