using System.Buffers.Binary;
namespace AzimuthRPC;

internal static class X64TelemetryStubs
{
    internal static byte[] BuildAbsoluteJump(ulong target)
    {
        byte[] patch = new byte[12];
        patch[0] = 0x48;
        patch[1] = 0xB8; // mov rax, imm64
        BinaryPrimitives.WriteUInt64LittleEndian(patch.AsSpan(2, 8), target);
        patch[10] = 0xFF;
        patch[11] = 0xE0; // jmp rax
        return patch;
    }

    internal static byte[] BuildRpmTrampoline(ulong telemetryAddress, ulong returnAddress)
    {
        var code = new List<byte>(96);

        // mov r10, telemetryAddress
        code.AddRange(new byte[] { 0x49, 0xBA });
        AddU64(code, telemetryAddress);

        // sequence++ (odd = writer active)
        code.AddRange(new byte[] { 0x41, 0xFF, 0x42, 0x04 });

        // movss xmm0,[r8] ; movss [r10],xmm0
        code.AddRange(new byte[] { 0xF3, 0x41, 0x0F, 0x10, 0x00, 0xF3, 0x41, 0x0F, 0x11, 0x02 });

        // movss xmm0,[r8+4] ; movss [r10+8],xmm0
        code.AddRange(new byte[] { 0xF3, 0x41, 0x0F, 0x10, 0x40, 0x04, 0xF3, 0x41, 0x0F, 0x11, 0x42, 0x08 });

        // shift = [r8+0x31]
        code.AddRange(new byte[] { 0x41, 0x0F, 0xB6, 0x40, 0x31, 0x41, 0x88, 0x42, 0x0C });

        // backfire = [r8+0x32]
        code.AddRange(new byte[] { 0x41, 0x0F, 0xB6, 0x40, 0x32, 0x41, 0x88, 0x42, 0x0D });

        // sequence++ (even = stable)
        code.AddRange(new byte[] { 0x41, 0xFF, 0x42, 0x04 });

        // Reproduce the 12 overwritten bytes exactly.
        code.AddRange(new byte[] { 0x0F, 0xB6, 0x41, 0x06, 0x2C, 0x02, 0x3C, 0x01, 0x41, 0x0F, 0x96, 0xC1 });

        // mov r11, returnAddress ; jmp r11
        code.AddRange(new byte[] { 0x49, 0xBB });
        AddU64(code, returnAddress);
        code.AddRange(new byte[] { 0x41, 0xFF, 0xE3 });

        return code.ToArray();
    }

    internal static byte[] BuildEventContextTrampoline(ulong telemetryAddress, ulong originalHandler)
    {
        var code = new List<byte>(80);

        // Wrap the previous handler rather than sampling before it. That way +0x60
        // is read after the game's Enter callback has finished updating the context.
        // Entry RSP is 8 mod 16; subtracting 0x38 supplies shadow space, two locals,
        // and keeps the stack 16-byte aligned for the nested call.
        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x38 });             // sub rsp,38h
        code.AddRange(new byte[] { 0x48, 0x89, 0x4C, 0x24, 0x20 });       // mov [rsp+20h],rcx

        code.AddRange(new byte[] { 0x49, 0xBB });                         // mov r11,originalHandler
        AddU64(code, originalHandler);
        code.AddRange(new byte[] { 0x41, 0xFF, 0xD3 });                   // call r11
        code.AddRange(new byte[] { 0x48, 0x89, 0x44, 0x24, 0x28 });       // mov [rsp+28h],rax

        code.AddRange(new byte[] { 0x49, 0xBA });                         // mov r10,telemetryAddress
        AddU64(code, telemetryAddress);
        code.AddRange(new byte[] { 0x41, 0xFF, 0x42, 0x14 });             // eventSequence++ (odd)
        code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x20 });       // mov rax,[rsp+20h]
        code.AddRange(new byte[] { 0x8B, 0x40, 0x60 });                   // mov eax,[rax+60h]
        code.AddRange(new byte[] { 0x41, 0x89, 0x42, 0x10 });             // mov [r10+10h],eax
        code.AddRange(new byte[] { 0x41, 0xFF, 0x42, 0x14 });             // eventSequence++ (even)

        code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x28 });       // restore RAX
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x38, 0xC3 });       // add rsp,38h ; ret
        return code.ToArray();
    }

    internal static void AddU64(List<byte> code, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        for (int i = 0; i < bytes.Length; i++) code.Add(bytes[i]);
    }

}
