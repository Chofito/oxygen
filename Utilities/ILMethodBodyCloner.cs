// Based on Nitrate mod's IntermediateLanguageUtil.cs
// Copyright (C) TeamCatalyst contributors — AGPL v3 (https://github.com/terraria-catalyst/nitrate-mod)
// Modifications: removed debug info cloning (SequencePoints, CustomDebugInformations)
// as they are not needed at runtime and add fragility.

using System;
using System.Linq;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace Oxygen.Utilities
{
    internal static class ILMethodBodyCloner
    {
        /// <summary>
        /// Clones <paramref name="source"/> into <paramref name="cursor"/>, completely
        /// replacing the target method body. Branch targets and exception handlers are
        /// remapped to the cloned instructions.
        /// </summary>
        /// <exception cref="Exception">
        /// Thrown if an instruction operand cannot be resolved during remapping.
        /// </exception>
        public static void CloneBodyToCursor(MethodBody source, ILCursor cursor)
        {
            cursor.Index = 0;
            cursor.Body.MaxStackSize = source.MaxStackSize;
            cursor.Body.InitLocals    = source.InitLocals;
            cursor.Body.LocalVarToken = source.LocalVarToken;

            // 1. Emit a clone of every instruction (operands may still point to old instructions).
            foreach (var instr in source.Instructions)
                cursor.Emit(instr.OpCode, instr.Operand);

            // 2. Preserve original offsets so IndexOf-based lookups work correctly.
            for (int i = 0; i < source.Instructions.Count; i++)
                cursor.Instrs[i].Offset = source.Instructions[i].Offset;

            // 3. Remap instruction-type operands (branch targets, switch tables) to their
            //    cloned counterparts.
            foreach (var instr in cursor.Body.Instructions)
            {
                instr.Operand = instr.Operand switch
                {
                    Instruction target =>
                        Resolve(target, source, cursor),

                    Instruction[] targets =>
                        targets.Select(t => Resolve(t, source, cursor)).ToArray(),

                    _ => instr.Operand
                };
            }

            // 4. Clone exception handlers with remapped boundaries.
            cursor.Body.ExceptionHandlers.AddRange(
                source.ExceptionHandlers.Select(h => new ExceptionHandler(h.HandlerType)
                {
                    TryStart     = h.TryStart     is null ? null : Resolve(h.TryStart,     source, cursor),
                    TryEnd       = h.TryEnd       is null ? null : Resolve(h.TryEnd,       source, cursor),
                    FilterStart  = h.FilterStart  is null ? null : Resolve(h.FilterStart,  source, cursor),
                    HandlerStart = h.HandlerStart is null ? null : Resolve(h.HandlerStart, source, cursor),
                    HandlerEnd   = h.HandlerEnd   is null ? null : Resolve(h.HandlerEnd,   source, cursor),
                    CatchType    = h.CatchType    is null ? null
                                   : cursor.Body.Method.Module.ImportReference(h.CatchType),
                })
            );

            // 5. Clone local variable declarations.
            cursor.Body.Variables.AddRange(
                source.Variables.Select(v => new VariableDefinition(v.VariableType))
            );

            cursor.Index = 0;
        }

        private static Instruction Resolve(Instruction src, MethodBody source, ILCursor dest)
        {
            int idx = source.Instructions.IndexOf(src);
            if (idx < 0)
                throw new Exception(
                    $"[Oxygen] ILMethodBodyCloner: could not resolve instruction " +
                    $"(opcode={src.OpCode.Name}, offset=0x{src.Offset:X4}) during clone.");
            return dest.Body.Instructions[idx];
        }
    }
}
