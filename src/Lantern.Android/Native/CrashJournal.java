package com.lantern.transfer;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Supplier;

/** Writes a bounded private crash report before delegating to Android's crash handler. */
public final class CrashJournal implements Thread.UncaughtExceptionHandler {
    private final File destination;
    private final Supplier<String> metadata;
    private final Thread.UncaughtExceptionHandler previous;
    private final AtomicBoolean recording=new AtomicBoolean();
    public CrashJournal(File file,Supplier<String> info,Thread.UncaughtExceptionHandler next) {
        destination=file;metadata=info;previous=next;
    }
    @Override public void uncaughtException(Thread thread,Throwable error) {
        try {
            if(recording.compareAndSet(false,true)) {
                StringWriter stack=new StringWriter();error.printStackTrace(new PrintWriter(stack));
                String text=metadata.get()+"\n线程："+thread.getName()+"\n完整 Java 异常：\n"+stack;
                if(text.length()>65536)text=text.substring(0,65536)+"\n[诊断已截断]";
                File pending=new File(destination.getPath()+".tmp");
                try(FileOutputStream output=new FileOutputStream(pending)) {
                    output.write(text.getBytes(StandardCharsets.UTF_8));output.flush();output.getFD().sync();
                }
                try {java.nio.file.Files.move(pending.toPath(),destination.toPath(),java.nio.file.StandardCopyOption.REPLACE_EXISTING,java.nio.file.StandardCopyOption.ATOMIC_MOVE);}
                catch(java.nio.file.AtomicMoveNotSupportedException unavailable) {java.nio.file.Files.move(pending.toPath(),destination.toPath(),java.nio.file.StandardCopyOption.REPLACE_EXISTING);}
            }
        } catch(Throwable ignored) {
            // Logging must never prevent Android from handling the original failure.
        } finally {
            if(previous!=null)previous.uncaughtException(thread,error);else System.exit(10);
        }
    }
}
