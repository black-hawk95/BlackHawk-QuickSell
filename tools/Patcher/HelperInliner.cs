using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Copy the small, self-contained runtime helper into QuickSell.dll. Importing its
// methods would leave a dependency on QuickSell.TestFixes.dll at game startup.
internal static class HelperInliner
{
    public static TypeDefinition Copy(ModuleDefinition source, ModuleDefinition destination)
    {
        var typeMap = new Dictionary<string, TypeDefinition>();
        var fieldMap = new Dictionary<string, FieldDefinition>();
        var methodMap = new Dictionary<string, MethodDefinition>();
        var typePairs = new List<(TypeDefinition Source, TypeDefinition Target)>();
        var methodPairs = new List<(MethodDefinition Source, MethodDefinition Target)>();

        TypeDefinition AddType(TypeDefinition original, TypeDefinition? parent = null)
        {
            if (destination.GetType(original.FullName) != null)
                throw new InvalidOperationException($"Helper type already exists: {original.FullName}");

            var target = new TypeDefinition(original.Namespace, original.Name, original.Attributes);
            if (parent == null) destination.Types.Add(target);
            else parent.NestedTypes.Add(target);
            typeMap.Add(original.FullName, target);
            typePairs.Add((original, target));
            foreach (var nested in original.NestedTypes) AddType(nested, target);
            return target;
        }

        AddType(source.Types.Single(t => t.FullName == "QuickSell.TestFixes.RuntimeFixes"));

        TypeReference Type(TypeReference reference)
        {
            if (typeMap.TryGetValue(reference.FullName, out var local)) return local;
            if (reference is GenericInstanceType generic)
            {
                var result = new GenericInstanceType(Type(generic.ElementType));
                foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(Type(argument));
                return result;
            }
            if (reference is ArrayType array) return new ArrayType(Type(array.ElementType), array.Rank);
            if (reference is ByReferenceType byRef) return new ByReferenceType(Type(byRef.ElementType));
            if (reference is PointerType pointer) return new PointerType(Type(pointer.ElementType));
            if (reference is OptionalModifierType optional)
                return new OptionalModifierType(Type(optional.ModifierType), Type(optional.ElementType));
            if (reference is RequiredModifierType required)
                return new RequiredModifierType(Type(required.ModifierType), Type(required.ElementType));
            if (reference is GenericParameter)
                throw new NotSupportedException("Unexpected generic parameter in runtime helper");
            return destination.ImportReference(reference);
        }

        foreach (var (original, target) in typePairs)
        {
            if (original.HasGenericParameters)
                throw new NotSupportedException($"Unexpected generic helper type: {original.FullName}");
            target.BaseType = original.BaseType == null ? null : Type(original.BaseType);
            foreach (var face in original.Interfaces)
                target.Interfaces.Add(new InterfaceImplementation(Type(face.InterfaceType)));
            foreach (var field in original.Fields)
            {
                var copy = new FieldDefinition(field.Name, field.Attributes, Type(field.FieldType));
                if (field.HasConstant) copy.Constant = field.Constant;
                target.Fields.Add(copy);
                fieldMap.Add(field.FullName, copy);
            }
            foreach (var method in original.Methods)
            {
                if (method.HasGenericParameters)
                    throw new NotSupportedException($"Unexpected generic helper method: {method.FullName}");
                var copy = new MethodDefinition(method.Name, method.Attributes, Type(method.ReturnType))
                {
                    ImplAttributes = method.ImplAttributes
                };
                foreach (var parameter in method.Parameters)
                    copy.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, Type(parameter.ParameterType)));
                target.Methods.Add(copy);
                methodMap.Add(method.FullName, copy);
                methodPairs.Add((method, copy));
            }
        }

        MethodReference Method(MethodReference method)
        {
            if (method is GenericInstanceMethod generic)
            {
                var result = new GenericInstanceMethod(Method(generic.ElementMethod));
                foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(Type(argument));
                return result;
            }
            return methodMap.TryGetValue(method.FullName, out var local)
                ? local : destination.ImportReference(method);
        }

        FieldReference Field(FieldReference field) => fieldMap.TryGetValue(field.FullName, out var local)
            ? local : destination.ImportReference(field);

        foreach (var (original, target) in typePairs)
        {
            foreach (var property in original.Properties)
            {
                var copy = new PropertyDefinition(property.Name, property.Attributes, Type(property.PropertyType));
                if (property.GetMethod != null) copy.GetMethod = methodMap[property.GetMethod.FullName];
                if (property.SetMethod != null) copy.SetMethod = methodMap[property.SetMethod.FullName];
                target.Properties.Add(copy);
            }
        }

        foreach (var (original, target) in methodPairs)
        {
            foreach (var overridden in original.Overrides) target.Overrides.Add(Method(overridden));
            if (!original.HasBody) continue;

            var oldBody = original.Body;
            var body = target.Body;
            body.InitLocals = oldBody.InitLocals;
            body.MaxStackSize = oldBody.MaxStackSize;
            foreach (var variable in oldBody.Variables) body.Variables.Add(new VariableDefinition(Type(variable.VariableType)));

            var instructionMap = new Dictionary<Instruction, Instruction>();
            var il = body.GetILProcessor();
            foreach (var instruction in oldBody.Instructions)
            {
                var copy = il.Create(OpCodes.Nop);
                copy.OpCode = instruction.OpCode;
                il.Append(copy);
                instructionMap.Add(instruction, copy);
            }

            foreach (var instruction in oldBody.Instructions)
            {
                var copy = instructionMap[instruction];
                copy.Operand = instruction.Operand switch
                {
                    Instruction branch => instructionMap[branch],
                    Instruction[] branches => branches.Select(b => instructionMap[b]).ToArray(),
                    VariableDefinition variable => body.Variables[oldBody.Variables.IndexOf(variable)],
                    ParameterDefinition parameter => target.Parameters[original.Parameters.IndexOf(parameter)],
                    TypeReference type => Type(type),
                    MethodReference method => Method(method),
                    FieldReference field => Field(field),
                    null => null,
                    _ => instruction.Operand
                };
            }

            foreach (var handler in oldBody.ExceptionHandlers)
            {
                body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
                {
                    CatchType = handler.CatchType == null ? null : Type(handler.CatchType),
                    TryStart = handler.TryStart == null ? null : instructionMap[handler.TryStart],
                    TryEnd = handler.TryEnd == null ? null : instructionMap[handler.TryEnd],
                    HandlerStart = handler.HandlerStart == null ? null : instructionMap[handler.HandlerStart],
                    HandlerEnd = handler.HandlerEnd == null ? null : instructionMap[handler.HandlerEnd],
                    FilterStart = handler.FilterStart == null ? null : instructionMap[handler.FilterStart]
                });
            }
        }

        if (destination.AssemblyReferences.Any(a => a.Name == source.Assembly.Name.Name))
            throw new InvalidOperationException("Inlining left a reference to the helper assembly");
        Console.WriteLine($"Inlined {typePairs.Count} helper type(s) into QuickSell.dll.");
        return typeMap["QuickSell.TestFixes.RuntimeFixes"];
    }
}
