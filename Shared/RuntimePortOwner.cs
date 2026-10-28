using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Desensitizer
{
 public enum PortOwnership
 {
  // Nothing is listening on the port yet.
  Nobody,
  // Every listener on the port belongs to the expected process.
  Child,
  // Some other process is listening. Never send anything to this port.
  Other
 }

 // Answers "which process is listening on this loopback port" from the OS TCP
 // table, so the app can confirm that the server it talks to is the child it
 // started and not another program that grabbed the free port first.
 public static class RuntimePortOwner
 {
  private const int AfInet=2,AfInet6=23,ListenerOwnerPid=3,NoError=0,InsufficientBuffer=122;

  [DllImport("iphlpapi.dll",SetLastError=true)]
  private static extern int GetExtendedTcpTable(IntPtr table,ref int size,[MarshalAs(UnmanagedType.Bool)] bool order,int family,int tableClass,uint reserved);

  public static PortOwnership Check(int port,int expectedProcessId)
  {
   var owners=ListenerProcessIds(port);
   if(owners.Count==0)return PortOwnership.Nobody;
   foreach(var owner in owners)if(owner!=expectedProcessId)return PortOwnership.Other;
   return PortOwnership.Child;
  }

  // Owning process ids of every IPv4 and IPv6 listener on the port. A table that
  // cannot be read is reported as an unknown foreign listener (id -1): fail closed.
  public static IReadOnlyList<int> ListenerProcessIds(int port)
  {
   var owners=new List<int>();
   // MIB_TCPROW_OWNER_PID: state, local address, local port (offset 8), remote address, remote port, pid (offset 20), 24 bytes.
   var v4=Read(AfInet,24,(buffer,offset)=>{ if(PortAt(buffer,offset+8)==port)owners.Add(Marshal.ReadInt32(buffer,offset+20)); });
   // MIB_TCP6ROW_OWNER_PID: local address (16), scope, local port (offset 20), remote address, scope, remote port, state, pid (offset 52), 56 bytes.
   var v6=Read(AfInet6,56,(buffer,offset)=>{ if(PortAt(buffer,offset+20)==port)owners.Add(Marshal.ReadInt32(buffer,offset+52)); });
   if(!v4||!v6)owners.Add(-1);
   return owners;
  }

  private static int PortAt(IntPtr buffer,int offset)
  {
   var raw=Marshal.ReadInt32(buffer,offset);
   return ((raw&0xFF)<<8)|((raw>>8)&0xFF);
  }

  private static bool Read(int family,int rowSize,Action<IntPtr,int> row)
  {
   for(var attempt=0;attempt<4;attempt++)
   {
    var size=0;
    var result=GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,ListenerOwnerPid,0);
    if(result!=InsufficientBuffer&&result!=NoError)return false;
    size+=4096;
    var buffer=Marshal.AllocHGlobal(size);
    try
    {
     result=GetExtendedTcpTable(buffer,ref size,false,family,ListenerOwnerPid,0);
     if(result==InsufficientBuffer)continue;
     if(result!=NoError)return false;
     var count=Marshal.ReadInt32(buffer);
     for(var index=0;index<count;index++)row(buffer,4+index*rowSize);
     return true;
    }
    finally{Marshal.FreeHGlobal(buffer);}
   }
   return false;
  }
 }
}
