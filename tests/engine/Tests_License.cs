using System.Security.Cryptography;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Licensing;

/// <summary>Licensing: signed activation keys, machine binding, 30-day trial, tamper and clock-rollback detection.</summary>
static class Tests_License
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  [LIC] {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  [LIC] {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
    }

    sealed class MemoryStore : ITrialStorage
    {
        public string? Value;
        public string? Read() => Value;
        public void Write(string value) => Value = value;
    }

    public static Task<int> RunAsync(string baseUrl, string served, string outDir, int port)
    {
        Console.WriteLine("Licensing");
        var home = Path.Combine(outDir, "license-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);

        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var machine = MachineCode.Compute("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "1A2B3C4D");
        var otherMachine = MachineCode.Compute("11111111-2222-3333-4444-555555555555", "1A2B3C4D");
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        // ---- machine code ----
        Check("machine code is XXXX-XXXX-XXXX-XXXX from an unambiguous alphabet",
            System.Text.RegularExpressions.Regex.IsMatch(machine, "^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$"), machine);
        Check("machine code is stable and differs between machines",
            machine == MachineCode.Compute("3F2504E0-4F89-11D3-9A0C-0305E82C3301 ", "1A2B3C4D") && machine != otherMachine);
        Check("this PC has a machine code", LicenseKey.NormalizeMachine(MachineCode.Current).Length == 16, MachineCode.Current);

        // ---- keys ----
        var yearKey = LicenseKey.Create(new LicenseInfo("خالد", machine, now, now.AddYears(1)), signer);
        Check("key round-trips name, machine and expiry",
            LicenseKey.TryVerify(yearKey, verifier, machine, out var info, out var err) && err == LicenseKeyError.None
            && info!.Name == "خالد" && info.ExpiresUtc == now.AddYears(1) && !info.IsLifetime);
        Check("key pasted with spaces, line breaks and a lower-case machine code still verifies",
            LicenseKey.TryVerify("  " + yearKey[..20] + "\r\n " + yearKey[20..] + "\n", verifier, machine.ToLowerInvariant().Replace("-", " "), out _, out _));
        Check("key for another machine is refused",
            !LicenseKey.TryVerify(yearKey, verifier, otherMachine, out _, out err) && err == LicenseKeyError.WrongMachine);
        Check("key signed by anyone else is refused",
            !LicenseKey.TryVerify(LicenseKey.Create(new LicenseInfo("x", machine, now, null), stranger), verifier, machine, out _, out err)
            && err == LicenseKeyError.BadSignature);
        var parts = yearKey.Split('.');
        LicenseKey.TryFromBase64Url(parts[1], out var payload);
        var forged = System.Text.Encoding.UTF8.GetString(payload).Replace("\"e\":", "\"e\":9");
        Check("editing the expiry inside a key breaks the signature",
            !LicenseKey.TryVerify(parts[0] + "." + LicenseKey.Base64Url(System.Text.Encoding.UTF8.GetBytes(forged)) + "." + parts[2], verifier, machine, out _, out err)
            && err == LicenseKeyError.BadSignature);
        Check("garbage and empty keys are refused without exceptions",
            !LicenseKey.TryVerify("hello", verifier, machine, out _, out err) && err == LicenseKeyError.Malformed
            && !LicenseKey.TryVerify("", verifier, machine, out _, out err) && err == LicenseKeyError.Empty
            && !LicenseKey.TryVerify("GDM1.@@@.###", verifier, machine, out _, out _));

        // ---- trial ----
        var clock = now;
        var fileA = new MemoryStore();
        var fileB = new MemoryStore();
        var licensePath = Path.Combine(home, "license.key");
        LicenseService Svc() => new(machine, verifier, [fileA, fileB], licensePath, () => clock);

        var s = Svc().Evaluate();
        Check("first start: 30-day trial with downloads allowed", s.State == LicenseState.Trial && s.DaysLeft == 30 && s.CanDownload, $"{s.State} {s.DaysLeft}");
        Check("trial start is written to every store", fileA.Value != null && fileA.Value == fileB.Value);

        clock = now.AddDays(12);
        s = Svc().Evaluate();
        Check("day 12: 18 days left", s.State == LicenseState.Trial && s.DaysLeft == 18, $"{s.DaysLeft}");

        var saved = fileA.Value;
        fileA.Value = null;
        clock = now.AddDays(13);
        s = Svc().Evaluate();
        Check("deleting one store does not restart the trial and it is healed", s.DaysLeft == 17 && fileA.Value != null, $"{s.DaysLeft}");

        clock = now.AddDays(31);
        s = Svc().Evaluate();
        Check("day 31: trial expired, downloads blocked", s.State == LicenseState.TrialExpired && !s.CanDownload, $"{s.State}");

        fileA.Value = fileB.Value = null;
        var old = new MemoryStore { Value = saved };
        s = new LicenseService(machine, verifier, [old, new MemoryStore()], licensePath, () => clock).Evaluate();
        Check("an old copy of the trial record still counts from the original start", s.State == LicenseState.TrialExpired);

        var edited = new MemoryStore { Value = "9999999999|9999999999|AAAAAAAAAAAAAAAAAAAAAA" };
        s = new LicenseService(machine, verifier, [edited], licensePath, () => now).Evaluate();
        Check("an edited trial record is treated as expired", s.State == LicenseState.TrialExpired);

        var copied = new MemoryStore { Value = saved };
        s = new LicenseService(otherMachine, verifier, [copied], licensePath, () => now).Evaluate();
        Check("a trial record copied from another PC is not accepted", s.State == LicenseState.TrialExpired);

        var rollA = new MemoryStore();
        clock = now.AddDays(20);
        new LicenseService(machine, verifier, [rollA], licensePath, () => clock).Evaluate();
        clock = now.AddDays(25);
        new LicenseService(machine, verifier, [rollA], licensePath, () => clock).Evaluate();
        clock = now.AddDays(10);
        s = new LicenseService(machine, verifier, [rollA], licensePath, () => clock).Evaluate();
        Check("turning the clock back is detected", s.State == LicenseState.ClockTampered && !s.CanDownload, $"{s.State}");

        // ---- activation ----
        clock = now.AddDays(40);
        var svc = Svc();
        var otherKey = LicenseKey.Create(new LicenseInfo("A", otherMachine, now, now.AddYears(1)), signer);
        Check("activation refuses another machine's key", svc.Activate(otherKey, out _) == LicenseKeyError.WrongMachine && !File.Exists(licensePath));
        var expiredKey = LicenseKey.Create(new LicenseInfo("A", machine, now, now.AddDays(30)), signer);
        Check("activation refuses an already expired key", svc.Activate(expiredKey, out _) == LicenseKeyError.Expired);
        Check("activation of a valid one-year key succeeds", svc.Activate(yearKey, out var act) == LicenseKeyError.None && act!.Name == "خالد");
        s = Svc().Evaluate();
        Check("licensed after activation, days left counted to the expiry", s.State == LicenseState.Licensed && s.CanDownload && s.DaysLeft == 325, $"{s.State} {s.DaysLeft}");
        clock = now.AddYears(1).AddDays(1);
        s = Svc().Evaluate();
        Check("the licence ends on its date", s.State == LicenseState.LicenseExpired && !s.CanDownload);

        var lifetime = LicenseKey.Create(new LicenseInfo("مدى الحياة", machine, now, null), signer);
        Check("a lifetime key activates", Svc().Activate(lifetime, out _) == LicenseKeyError.None);
        clock = now.AddYears(30);
        s = Svc().Evaluate();
        Check("a lifetime licence never expires", s.State == LicenseState.Licensed && s.License!.IsLifetime);

        File.WriteAllText(licensePath, "GDM1.tampered.key");
        s = Svc().Evaluate();
        Check("a damaged licence file falls back to the trial state", s.State == LicenseState.TrialExpired);

        Check("the shipped public key loads", LicensePublicKey.Load().KeySize == 256);

        try { Directory.Delete(home, true); } catch (IOException) { /* best-effort cleanup of test output */ }
        Console.WriteLine($"[LIC Summary] {_pass} passed, {_fail} failed");
        return Task.FromResult(_fail);
    }
}
