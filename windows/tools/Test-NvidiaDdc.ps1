# Read-only monitor diagnostic via the NVIDIA driver. No Set VCP command is sent.
# API layouts: https://github.com/NVIDIA/nvapi (NV_I2C_INFO_V3).
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class NvidiaDdcProbe {
    [DllImport("nvapi64.dll", CallingConvention=CallingConvention.Cdecl)]
    static extern IntPtr nvapi_QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumDisplay(uint index, out IntPtr display);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Name(IntPtr display, StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Output(IntPtr display, out uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Gpus(IntPtr display, [Out] IntPtr[] gpus, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int I2c(IntPtr gpu, ref Info info);
    [StructLayout(LayoutKind.Sequential)] struct Info {
        public uint Version, DisplayMask;
        public byte IsDdc, Address;
        public IntPtr Register;
        public uint RegisterSize;
        public IntPtr Data;
        public uint Size, Speed, SpeedKhz;
        public byte Port;
        public uint PortSet;
    }
    static T Get<T>(uint id) where T:class {
        var p = nvapi_QueryInterface(id);
        if(p == IntPtr.Zero) throw new Exception("NVIDIA API unavailable: " + id.ToString("X"));
        return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
    }
    static int Transfer(I2c function, IntPtr gpu, uint mask, byte address, byte[] bytes, bool read, bool registerZero) {
        var data = Marshal.AllocHGlobal(bytes.Length);
        var reg = registerZero ? Marshal.AllocHGlobal(1) : IntPtr.Zero;
        try {
            Marshal.Copy(bytes,0,data,bytes.Length);
            if(registerZero) Marshal.WriteByte(reg,0);
            var info = new Info { Version=(uint)Marshal.SizeOf(typeof(Info)) | (3u<<16), DisplayMask=mask,
                IsDdc=1, Address=address, Register=reg, RegisterSize=registerZero?1u:0u,
                Data=data, Size=(uint)bytes.Length, Speed=65535, SpeedKhz=0 };
            int status=function(gpu,ref info);
            if(read && status==0) Marshal.Copy(data,bytes,0,bytes.Length);
            return status;
        } finally { Marshal.FreeHGlobal(data); if(reg!=IntPtr.Zero) Marshal.FreeHGlobal(reg); }
    }
    public static string[] Run() {
        var result=new List<string>();
        int init=Get<Init>(0x0150e828)();
        result.Add("Initialize="+init+" I2CStructBytes="+Marshal.SizeOf(typeof(Info)));
        if(init!=0) return result.ToArray();
        var enumerate=Get<EnumDisplay>(0x9abdd40d);
        var name=Get<Name>(0x22a78b05);
        var output=Get<Output>(0xd995937e);
        var gpus=Get<Gpus>(0x34ef9506);
        var read=Get<I2c>(0x2fde12c5);
        var write=Get<I2c>(0xe812eb07);
        for(uint i=0;i<16;i++) {
            IntPtr display; int e=enumerate(i,out display);
            if(e!=0) { result.Add("EnumerationEnd="+e); break; }
            var label=new StringBuilder(64); name(display,label);
            uint mask, count; var handles=new IntPtr[64];
            int o=output(display,out mask), g=gpus(display,handles,out count);
            result.Add(label+" output="+mask+" outputStatus="+o+" gpuStatus="+g+" gpuCount="+count);
            if(o!=0 || g!=0 || count!=1) continue;
            var edid=new byte[16];
            int er=Transfer(read,handles[0],mask,0xa0,edid,true,true);
            result.Add("EDIDRead="+er+" bytes="+BitConverter.ToString(edid));
            // DDC/CI Get VCP(0x60): address 0x6e, source 0x51, length 0x82,
            // opcode 0x01, feature 0x60, XOR checksum 0xdc. This only queries.
            int request=Transfer(write,handles[0],mask,0x6e,new byte[]{0x51,0x82,0x01,0x60,0xdc},false,false);
            Thread.Sleep(80);
            var reply=new byte[11];
            int response=Transfer(read,handles[0],mask,0x6e,reply,true,false);
            byte checksum=0x50; foreach(byte b in reply) checksum^=b;
            bool valid=response==0 && reply[0]==0x6e && reply[1]==0x88 && reply[2]==0x02 && reply[3]==0 && reply[4]==0x60 && checksum==0;
            result.Add("GetInputRequest="+request+" read="+response+" valid="+valid+" value="+(valid?((reply[8]<<8)|reply[9]).ToString():"unknown")+" raw="+BitConverter.ToString(reply));
        }
        Get<Init>(0xd22bdd7e)();
        return result.ToArray();
    }
}
'@
[NvidiaDdcProbe]::Run()
