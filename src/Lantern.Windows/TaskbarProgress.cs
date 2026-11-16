using System.Runtime.InteropServices;
using Lantern.AppLogic;

namespace Lantern.Windows;

public enum TaskbarState : uint { None=0, Indeterminate=1, Normal=2, Error=4, Paused=8 }
public record TaskbarFrame(TaskbarState State,ulong Completed=0,ulong Total=100);

public sealed class TaskbarProgressReducer
{
    private readonly HashSet<string> finished=[];
    private long errorUntil;
    public TaskbarFrame Update(TransferNotificationSnapshot snapshot,bool enabled,long now)
    {
        bool freshFailure=false;
        foreach(var task in snapshot.Tasks.Where(t=>TransferNotificationReducer.IsFinished(t.State)))
            if(finished.Add(task.Id)&&task.State=="failed")freshFailure=true;
        if(!enabled){errorUntil=0;return new(TaskbarState.None);}
        if(freshFailure)errorUntil=now+5000;
        var active=snapshot.Tasks.Where(t=>!TransferNotificationReducer.IsFinished(t.State)).ToArray();
        if(now<errorUntil)return new(TaskbarState.Error,1,1);
        if(active.Length==0)return new(TaskbarState.None);
        decimal total=active.Sum(t=>(decimal)Math.Max(0,t.Total));
        decimal done=active.Sum(t=>(decimal)Math.Clamp(t.Completed,0,Math.Max(0,t.Total)));
        bool paused=active.All(t=>t.State=="paused");
        if(!paused&&(total==0||!active.Any(t=>t.State=="transferring")))return new(TaskbarState.Indeterminate);
        // Keep 100% reserved for verified completion; totals use decimal to avoid overflow.
        ulong progress=total>0?(ulong)Math.Min(9999,decimal.Floor(done*10000/total)):0;
        return new(paused?TaskbarState.Paused:TaskbarState.Normal,progress,10000);
    }
}

public sealed class TaskbarProgress : IDisposable
{
    private ITaskbarList3? shell;
    private TaskbarFrame? previous;
    public TaskbarFrame? AppliedFrame=>previous;
    private readonly TaskbarProgressReducer reducer=new();
    public void ButtonCreated()
    {
        Release();
        try{shell=(ITaskbarList3)new TaskbarList();shell.HrInit();}
        catch(Exception ex) when(ex is COMException or InvalidCastException){Release();}
    }
    public void Update(IntPtr window,TransferNotificationSnapshot snapshot,bool enabled)
    {
        var frame=reducer.Update(snapshot,enabled,Environment.TickCount64);
        if(shell is null||frame==previous)return;
        try
        {
            shell.SetProgressState(window,frame.State);
            if(frame.State is TaskbarState.Normal or TaskbarState.Paused or TaskbarState.Error)
                shell.SetProgressValue(window,frame.Completed,frame.Total);
            previous=frame;
        }
        catch(COMException){Release();}
    }
    private void Release(){previous=null;if(shell is not null){Marshal.ReleaseComObject(shell);shell=null;}}
    public void Dispose()=>Release();
    [ComImport,Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] private class TaskbarList { }
    // ITaskbarList / ITaskbarList2 methods precede the ITaskbarList3 progress methods.
    [ComImport,Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();void AddTab(IntPtr hwnd);void DeleteTab(IntPtr hwnd);void ActivateTab(IntPtr hwnd);void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd,[MarshalAs(UnmanagedType.Bool)]bool fullscreen);
        void SetProgressValue(IntPtr hwnd,ulong completed,ulong total);
        void SetProgressState(IntPtr hwnd,TaskbarState state);
    }
}
