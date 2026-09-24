using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Patcher <QuickSell.dll> <QuickSell.TestFixes.dll>");
            return 2;
        }

        var quickSellPath = Path.GetFullPath(args[0]);
        var helperPath = Path.GetFullPath(args[1]);

        var quick = AssemblyDefinition.ReadAssembly(quickSellPath);
        var helper = AssemblyDefinition.ReadAssembly(helperPath);
        var module = quick.MainModule;

        var helperType = helper.MainModule.Types.First(t => t.FullName == "QuickSell.TestFixes.RuntimeFixes");
        MethodReference ImportHelper(string name)
        {
            var method = helperType.Methods.Single(m => m.Name == name);
            return module.ImportReference(method);
        }

        PatchAwake(module, ImportHelper("Initialize"));
        PatchTooltipGuard(module, ImportHelper("ShouldSkipTooltip"));
        PatchSoundGate(module, ImportHelper("PlaySellSoundPerItem"));
        PatchTraderOffer(module, ImportHelper("GetBestTraderOffer"));
        PatchFleaComposite(module, "QuickSell.Patches.ContextMenuPatch", "ShowFleaConfirmation", ImportHelper("GetCompositeFleaPrice"), itemIsArgument: false);
        PatchFleaComposite(module, "QuickSell.Patches.TooltipPatch", "BuildPriceLines", ImportHelper("GetCompositeFleaPrice"), itemIsArgument: true);

        var temp = quickSellPath + ".patched";
        quick.Write(temp);
        quick.Dispose();
        helper.Dispose();

        File.Copy(temp, quickSellPath, true);
        File.Delete(temp);

        Console.WriteLine("Patched QuickSell.dll successfully.");
        return 0;
    }

    private static TypeDefinition FindType(ModuleDefinition module, string fullName)
    {
        foreach (var type in module.Types)
        {
            var found = FindTypeRecursive(type, fullName);
            if (found != null) return found;
        }
        throw new InvalidOperationException($"Type not found: {fullName}");
    }

    private static TypeDefinition? FindTypeRecursive(TypeDefinition type, string fullName)
    {
        if (type.FullName == fullName || type.Name == fullName) return type;
        foreach (var nested in type.NestedTypes)
        {
            var found = FindTypeRecursive(nested, fullName);
            if (found != null) return found;
        }
        return null;
    }

    private static void PatchAwake(ModuleDefinition module, MethodReference init)
    {
        var type = FindType(module, "QuickSell.Plugin");
        var method = type.Methods.Single(m => m.Name == "Awake" && !m.HasParameters);
        var call = method.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is MethodReference mr &&
            mr.Name == "BindSellingSettings");

        if (call == null) throw new InvalidOperationException("BindSellingSettings call not found in Plugin.Awake");

        var il = method.Body.GetILProcessor();
        var next = call.Next ?? throw new InvalidOperationException("Awake insertion point missing");
        il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(next, il.Create(OpCodes.Call, init));
        method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 2);
        Console.WriteLine("Patched F12 sound setting initialization.");
    }

    private static void PatchTooltipGuard(ModuleDefinition module, MethodReference shouldSkip)
    {
        var type = FindType(module, "QuickSell.Patches.TooltipPatch");
        var method = type.Methods.Single(m => m.Name == "Prefix");
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions.First();

        il.InsertBefore(first, il.Create(OpCodes.Call, shouldSkip));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 1);
        Console.WriteLine("Patched trader-inventory tooltip suppression.");
    }

    private static void PatchSoundGate(ModuleDefinition module, MethodReference playEach)
    {
        var contextType = FindType(module, "QuickSell.Patches.ContextMenuPatch");
        var gate = contextType.NestedTypes.FirstOrDefault(t => t.Name == "SellSoundGate")
            ?? throw new InvalidOperationException("SellSoundGate not found");
        var method = gate.Methods.Single(m => m.Name == "OnResult");
        var played = gate.Fields.Single(f => f.Name == "_played");

        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions.First();

        il.InsertBefore(first, il.Create(OpCodes.Call, playEach));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, il.Create(OpCodes.Stfld, played));
        method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 2);
        Console.WriteLine("Patched per-item sell sound toggle.");
    }

    private static void PatchTraderOffer(ModuleDefinition module, MethodReference getBestOffer)
    {
        var type = FindType(module, "QuickSell.Patches.TraderService");
        var method = type.Methods.Single(m => m.Name == "TryGetBestOffer" && m.Parameters.Count == 4);

        var traderByRef = method.Parameters[2].ParameterType as ByReferenceType
            ?? throw new InvalidOperationException("bestTrader is not by-ref");
        var traderType = traderByRef.ElementType;

        method.Body.ExceptionHandlers.Clear();
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.InitLocals = true;

        var resultVar = new VariableDefinition(new ArrayType(module.TypeSystem.Object));
        method.Body.Variables.Add(resultVar);

        var il = method.Body.GetILProcessor();

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Call, getBestOffer));
        il.Append(il.Create(OpCodes.Stloc, resultVar));

        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Ldloc, resultVar));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Castclass, traderType));
        il.Append(il.Create(OpCodes.Stind_Ref));

        il.Append(il.Create(OpCodes.Ldarg_3));
        il.Append(il.Create(OpCodes.Ldloc, resultVar));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Unbox_Any, module.TypeSystem.Int32));
        il.Append(il.Create(OpCodes.Stind_I4));

        il.Append(il.Create(OpCodes.Ldloc, resultVar));
        il.Append(il.Create(OpCodes.Ldc_I4_2));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Unbox_Any, module.TypeSystem.Boolean));
        il.Append(il.Create(OpCodes.Ret));

        method.Body.MaxStackSize = 4;
        Console.WriteLine("Patched composite trader pricing.");
    }

    private static void PatchFleaComposite(
        ModuleDefinition module,
        string typeName,
        string methodName,
        MethodReference composite,
        bool itemIsArgument)
    {
        var type = FindType(module, typeName);
        var method = type.Methods.First(m => m.Name == methodName);
        var calls = method.Body.Instructions
            .Where(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand is MethodReference mr &&
                mr.Name == "TryGet" &&
                mr.DeclaringType.FullName == "QuickSell.Patches.FleaPriceCache")
            .ToList();

        if (calls.Count == 0)
            throw new InvalidOperationException($"FleaPriceCache.TryGet call not found in {typeName}.{methodName}");

        // There is one relevant TryGet in each target method.
        var call = calls[0];
        VariableDefinition? priceVar = null;

        for (var p = call.Previous; p != null; p = p.Previous)
        {
            if (p.OpCode == OpCodes.Ldloca || p.OpCode == OpCodes.Ldloca_S)
            {
                priceVar = (VariableDefinition)p.Operand;
                break;
            }
            if (Distance(p, call) > 12) break;
        }

        if (priceVar == null)
            throw new InvalidOperationException($"Could not identify flea price local in {methodName}");

        var branch = call.Next;
        if (branch == null || (branch.OpCode != OpCodes.Brfalse && branch.OpCode != OpCodes.Brfalse_S))
            throw new InvalidOperationException($"Expected brfalse after TryGet in {methodName}");

        var successPoint = branch.Next
            ?? throw new InvalidOperationException($"Missing success path in {methodName}");

        var il = method.Body.GetILProcessor();

        if (itemIsArgument)
        {
            il.InsertBefore(successPoint, il.Create(OpCodes.Ldarg_0));
        }
        else
        {
            var candidateLoad = FindCandidateLoad(call);
            if (candidateLoad == null)
                throw new InvalidOperationException("Could not identify candidate item local in ShowFleaConfirmation");

            il.InsertBefore(successPoint, CloneLoad(il, candidateLoad));
        }

        il.InsertBefore(successPoint, il.Create(OpCodes.Ldloc, priceVar));
        il.InsertBefore(successPoint, il.Create(OpCodes.Call, composite));
        il.InsertBefore(successPoint, il.Create(OpCodes.Stloc, priceVar));

        method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 3);
        Console.WriteLine($"Patched composite flea pricing in {typeName}.{methodName}.");
    }

    private static Instruction? FindCandidateLoad(Instruction call)
    {
        var p = call.Previous;
        var count = 0;
        while (p != null && count++ < 20)
        {
            if ((p.OpCode == OpCodes.Call || p.OpCode == OpCodes.Callvirt) &&
                p.Operand is MethodReference mr &&
                mr.Name == "get_TemplateId")
            {
                var load = p.Previous;
                if (load != null && IsLoadLocal(load))
                    return load;
            }
            p = p.Previous;
        }
        return null;
    }

    private static bool IsLoadLocal(Instruction i) =>
        i.OpCode == OpCodes.Ldloc ||
        i.OpCode == OpCodes.Ldloc_S ||
        i.OpCode == OpCodes.Ldloc_0 ||
        i.OpCode == OpCodes.Ldloc_1 ||
        i.OpCode == OpCodes.Ldloc_2 ||
        i.OpCode == OpCodes.Ldloc_3;

    private static Instruction CloneLoad(ILProcessor il, Instruction source)
    {
        if (source.OpCode == OpCodes.Ldloc_0) return il.Create(OpCodes.Ldloc_0);
        if (source.OpCode == OpCodes.Ldloc_1) return il.Create(OpCodes.Ldloc_1);
        if (source.OpCode == OpCodes.Ldloc_2) return il.Create(OpCodes.Ldloc_2);
        if (source.OpCode == OpCodes.Ldloc_3) return il.Create(OpCodes.Ldloc_3);
        if (source.Operand is VariableDefinition v)
            return il.Create(source.OpCode, v);
        throw new InvalidOperationException("Unsupported local load");
    }

    private static int Distance(Instruction from, Instruction to)
    {
        var n = 0;
        for (var p = from; p != null && p != to && n < 100; p = p.Next) n++;
        return n;
    }
}
