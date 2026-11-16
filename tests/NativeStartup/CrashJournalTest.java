import com.lantern.transfer.CrashJournal;
import java.io.*;
import java.nio.file.*;
import java.util.concurrent.atomic.AtomicInteger;

public final class CrashJournalTest {
    public static void main(String[] args) throws Exception {
        Path root=Files.createTempDirectory(Paths.get(args[0]),"crash-journal-");
        File report=root.resolve("java-crash.txt").toFile();Files.writeString(report.toPath(),"old report");
        AtomicInteger delegated=new AtomicInteger();
        Throwable nested=new IllegalStateException("Unable to instantiate provider",new UnsatisfiedLinkError("JNI method not found"));
        CrashJournal handler=new CrashJournal(report,()->"Lantern test\n阶段：Provider init",(thread,error)-> {
            if(error!=nested)throw new AssertionError("Original exception changed");
            try {String text=Files.readString(report.toPath());if(!text.contains("Caused by: java.lang.UnsatisfiedLinkError: JNI method not found")||!text.contains("阶段：Provider init"))throw new AssertionError("Missing causal stack or phase");}
            catch(IOException failure){throw new AssertionError(failure);}
            delegated.incrementAndGet();
        });
        handler.uncaughtException(Thread.currentThread(),nested);
        if(delegated.get()!=1)throw new AssertionError("Platform handler was not delegated exactly once");
        CrashJournal broken=new CrashJournal(root.resolve("missing-parent/report.txt").toFile(),()->"test",(t,e)->delegated.incrementAndGet());
        broken.uncaughtException(Thread.currentThread(),nested);
        if(delegated.get()!=2)throw new AssertionError("Logging failure suppressed platform handler");
        String huge="X".repeat(100000);
        new CrashJournal(report,()->huge,(t,e)->{}).uncaughtException(Thread.currentThread(),nested);
        if(Files.readString(report.toPath()).length()>65600)throw new AssertionError("Report not bounded");
        System.out.println("PASS: causal Java stack saved before delegation, previous report replaced, logging failure delegates, report size bounded");
    }
}
