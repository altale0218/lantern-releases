package com.lantern.transfer;

import android.app.Activity;
import android.app.ActivityManager;
import android.app.ApplicationExitInfo;
import android.os.Build;
import android.os.Bundle;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.widget.*;
import java.io.InputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;

/** Pure framework entry point: the :startup process does not load .NET. */
public final class StartupActivity extends Activity {
    private boolean opening;
    private SharedPreferences diagnostics;
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        diagnostics=getSharedPreferences("lantern.startup",MODE_PRIVATE);
        boolean dark=startupDark();
        setContentView(new BrandSplashView(this,dark));
        StartupWindowStyle.install(getWindow(),dark);
        // Reading system traces can block, so never do it on the UI thread.
        new Thread(() -> {
            String report=null;
            try { report=readLastCrash(); } catch(Exception ignored) {}
            final String text=report;
            runOnUiThread(() -> {if(isFinishing()||isDestroyed())return;if(text==null)openMain();else showReport(text);});
        },"Lantern startup diagnostics").start();
    }
    private boolean startupDark() {
        boolean system=(getResources().getConfiguration().uiMode & 48)==32;
        try {
            File file=new File(getFilesDir(),"settings.json");
            if(file.length()>32768)return system;
            try(FileInputStream stream=new FileInputStream(file)) {
                ByteArrayOutputStream data=new ByteArrayOutputStream();byte[] block=new byte[1024];int count;
                while((count=stream.read(block))>0&&data.size()<32768)data.write(block,0,count);
                String theme=new org.json.JSONObject(data.toString("UTF-8")).optString("theme","system");
                return theme.equals("dark")||(!theme.equals("light")&&system);
            }
        }catch(Exception ignored){return system;}
    }
    private String readLastCrash() throws Exception {
        // Startup has its own process and journal, so main-process-only diagnostics miss these crashes.
        File startup=new File(getFilesDir(),"startup-java-crash.txt");
        if(startup.isFile()&&startup.lastModified()>diagnostics.getLong("reportedStartupJavaAt",0)) {
            try(FileInputStream input=new FileInputStream(startup)) {
                ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int count;
                while(bytes.size()<262144&&(count=input.read(buffer,0,Math.min(buffer.length,262144-bytes.size())))>0)bytes.write(buffer,0,count);
                diagnostics.edit().putLong("pendingStartupJavaAt",startup.lastModified()).apply();
                return new String(bytes.toByteArray(),java.nio.charset.StandardCharsets.UTF_8);
            }
        }
        File local=new File(getFilesDir(),"java-crash.txt");
        if(local.isFile()&&local.lastModified()>diagnostics.getLong("reportedJavaAt",0)) {
            long timestamp=local.lastModified(),systemTimestamp=0;
            if(Build.VERSION.SDK_INT>=30) {
                try {
                    ActivityManager manager=(ActivityManager)getSystemService(ACTIVITY_SERVICE);
                    for(ApplicationExitInfo exit:manager.getHistoricalProcessExitReasons(getPackageName(),0,16))
                        if(getPackageName().equals(exit.getProcessName()))systemTimestamp=Math.max(systemTimestamp,exit.getTimestamp());
                } catch(Exception ignored) {}
            }
            try(FileInputStream input=new FileInputStream(local)) {
                ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int count;
                while(bytes.size()<262144&&(count=input.read(buffer,0,Math.min(buffer.length,262144-bytes.size())))>0)bytes.write(buffer,0,count);
                diagnostics.edit().putLong("pendingJavaAt",timestamp).putLong("pendingAt",systemTimestamp).apply();
                return new String(bytes.toByteArray(),java.nio.charset.StandardCharsets.UTF_8);
            }
        }
        if(Build.VERSION.SDK_INT<30)return null;
        ActivityManager manager=(ActivityManager)getSystemService(ACTIVITY_SERVICE);
        long seen=diagnostics.getLong("reportedAt",0);
        for(ApplicationExitInfo exit:manager.getHistoricalProcessExitReasons(getPackageName(),0,16)) {
            if(!getPackageName().equals(exit.getProcessName())||exit.getTimestamp()<=seen)continue;
            int reason=exit.getReason();
            if(reason!=ApplicationExitInfo.REASON_CRASH&&reason!=ApplicationExitInfo.REASON_CRASH_NATIVE
                &&reason!=ApplicationExitInfo.REASON_INITIALIZATION_FAILURE&&reason!=ApplicationExitInfo.REASON_ANR
                &&reason!=ApplicationExitInfo.REASON_SIGNALED)continue;
            StringBuilder text=new StringBuilder("Lantern Android 0.7.30\n系统记录的上次异常退出\n设备：")
                .append(Build.MANUFACTURER).append(" ").append(Build.MODEL)
                .append("\nAndroid：").append(Build.VERSION.RELEASE).append(" / API ").append(Build.VERSION.SDK_INT)
                .append("\n时间：").append(new java.util.Date(exit.getTimestamp()))
                .append("\n进程：").append(exit.getProcessName())
                .append("\n退出原因：").append(reason).append(" / 状态：").append(exit.getStatus())
                .append("\n说明：").append(exit.getDescription()).append("\n\n");
            try(InputStream trace=exit.getTraceInputStream()) {
                if(trace!=null) {
                    ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int count;
                    while(bytes.size()<65536&&(count=trace.read(buffer,0,Math.min(buffer.length,65536-bytes.size())))>0)bytes.write(buffer,0,count);
                    // Native traces can be protobuf tombstones. Preserve bytes in a shareable encoding.
                    byte[] raw=bytes.toByteArray();boolean binary=false;
                    for(byte b:raw)if(b==0){binary=true;break;}
                    if(binary)text.append("系统 trace（Base64）：\n").append(android.util.Base64.encodeToString(raw,android.util.Base64.NO_WRAP));
                    else text.append(new String(raw,java.nio.charset.StandardCharsets.UTF_8));
                } else text.append("系统未保留详细堆栈；请复制以上退出原因。");
            } catch(Exception e) {text.append("系统堆栈暂不可读取：").append(e.getClass().getSimpleName());}
            // Retain the report until the user explicitly chooses to retry.
            diagnostics.edit().putLong("pendingAt",exit.getTimestamp()).apply();
            return text.toString();
        }
        return null;
    }
    private void showReport(String report) {
        LinearLayout panel=new LinearLayout(this);panel.setOrientation(LinearLayout.VERTICAL);
        int pad=(int)(20*getResources().getDisplayMetrics().density);panel.setPadding(pad,pad*2,pad,pad);
        panel.setBackgroundColor(Color.rgb(245,247,250));
        TextView heading=new TextView(this);heading.setText("检测到上次启动异常\n请先复制诊断信息，再重试启动。");heading.setTextSize(19);heading.setTextColor(Color.rgb(24,32,44));panel.addView(heading);
        Button copy=new Button(this);copy.setText("复制诊断信息");copy.setOnClickListener(v -> {
            ClipboardManager clipboard=(ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
            clipboard.setPrimaryClip(ClipData.newPlainText("Lantern 启动诊断",report));Toast.makeText(this,"诊断已复制",Toast.LENGTH_SHORT).show();
        });panel.addView(copy);
        Button retry=new Button(this);retry.setText("重试启动");retry.setOnClickListener(v -> {
            diagnostics.edit().putLong("reportedAt",Math.max(diagnostics.getLong("reportedAt",0),diagnostics.getLong("pendingAt",0)))
                .putLong("reportedJavaAt",Math.max(diagnostics.getLong("reportedJavaAt",0),diagnostics.getLong("pendingJavaAt",0)))
                .putLong("reportedStartupJavaAt",Math.max(diagnostics.getLong("reportedStartupJavaAt",0),diagnostics.getLong("pendingStartupJavaAt",0))).commit();openMain();
        });panel.addView(retry);
        TextView details=new TextView(this);details.setText(report);details.setTextSize(14);details.setTextColor(Color.rgb(24,32,44));details.setTextIsSelectable(true);panel.addView(details);
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);scroll.addView(panel);setContentView(scroll);
    }
    private void openMain() {
        if(opening)return;opening=true;
        try {startActivity(new Intent().setClassName(getPackageName(),"com.lantern.transfer.MainActivity"));finish();}
        catch(Exception e){opening=false;showReport("Lantern 0.7.30\n无法打开主界面："+e);}
    }
}
