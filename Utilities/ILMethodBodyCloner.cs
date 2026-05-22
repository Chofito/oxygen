using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace Oxygen.Utilities
{
    internal static class ILMethodBodyCloner
    {
        // Clones the instruction body of `source` into `cursor`, adjusting all
        // branch targets and exception handler boundaries to point to the new copies.
        // Debug sequence points and custom debug info are intentionally not cloned —
        // the target method is a generated stub, not a user-facing call site.
        public static void CloneBodyToCursor(MethodBody source, ILCursor cursor)
        {
            cursor.Index = 0;
            cursor.Body.MaxStackSize = source.MaxStackSize;
            cursor.Body.InitLocals   = source.InitLocals;
            cursor.Body.LocalVarToken = source.LocalVarToken;

            // Emit a copy of every instruction. Operands that reference other
            // instructions in the source body are still pointing at those source
            // instructions — we'll fix them in the remap step below.
            foreach (var instr in source.Instructions)
                cursor.Emit(instr.OpCode, instr.Operand);

            // Preserve the original IL offsets so offset-based lookups (e.g. from
            // exception handler range checks) continue to work correctly.
            for (int i = 0; i < source.Instructions.Count; i++)
                cursor.Instrs[i].Offset = source.Instructions[i].Offset;

            // Build a O(1) map: source instruction → its clone in the destination body.
            var map = BuildInstructionMap(source, cursor);

            // Remap all instruction-type operands (branch targets, switch tables).
            foreach (var instr in cursor.Body.Instructions)
            {
                instr.Operand = instr.Operand switch
                {
                    Instruction target     => map[target],
                    Instruction[] targets  => Array.ConvertAll(targets, t => map[t]),
                    _                      => instr.Operand
                };
            }

            // Clone exception handlers with remapped boundaries.
            cursor.Body.ExceptionHandlers.AddRange(
                source.ExceptionHandlers.Select(h => new ExceptionHandler(h.HandlerType)
                {
                    TryStart     = h.TryStart     is null ? null : map[h.TryStart],
                    TryEnd       = h.TryEnd       is null ? null : map[h.TryEnd],
                    FilterStart  = h.FilterStart  is null ? null : map[h.FilterStart],
                    HandlerStart = h.HandlerStart is null ? null : map[h.HandlerStart],
                    HandlerEnd   = h.HandlerEnd   is null ? null : map[h.HandlerEnd],
                    CatchType    = h.CatchType    is null ? null
                                   : cursor.Body.Method.Module.ImportReference(h.CatchType),
                })
            );

            // Clone local variable declarations (types only; names are not needed).
            cursor.Body.Variables.AddRange(
                source.Variables.Select(v => new VariableDefinition(v.VariableType))
            );

            cursor.Index = 0;
        }

        private static Dictionary<Instruction, Instruction> BuildInstructionMap(
            MethodBody source, ILCursor dest)
        {
            var srcInstrs  = source.Instructions;
            var destInstrs = dest.Body.Instructions;

            if (srcInstrs.Count != destInstrs.Count)
                throw new Exception(
                    $"Instruction count mismatch after clone: " +
                    $"source={srcInstrs.Count}, dest={destInstrs.Count}.");

            var map = new Dictionary<Instruction, Instruction>(srcInstrs.Count);
            for (int i = 0; i < srcInstrs.Count; i++)
                map[srcInstrs[i]] = destInstrs[i];

            return map;
        }
    }
}
