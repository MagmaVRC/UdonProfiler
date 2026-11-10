using System.Collections.Generic;
using NUnit.Framework;
using VRC.Udon.Common.Interfaces;

namespace MagmaVRC.UdonProfiler.Tests
{
    public class StockHookTests
    {
        const string CallReturn = @"
.data_start
    a: %SystemInt32, 3
    r: %SystemInt32, 0
    ret: %SystemUInt32, null
    ret1: %SystemUInt32, 0x10
    ret2: %SystemUInt32, 0x20
.data_end
.code_start
    .export _start
    _start:
    PUSH, ret1
    JUMP, add
    PUSH, ret2
    JUMP, add
    JUMP, 0xFFFFFFFC
    add:
    PUSH, r
    PUSH, a
    PUSH, r
    EXTERN, ""SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32""
    PUSH, ret
    COPY
    JUMP_INDIRECT, ret
.code_end
";

        const string Jif = @"
.data_start
    c: %SystemBoolean, null
.data_end
.code_start
    .export _start
    _start:
    PUSH, c
    JUMP_IF_FALSE, skip
    NOP
    skip:
    JUMP, 0xFFFFFFFC
.code_end
";

        bool _enabled;
        System.Action<uint, uint, byte> _observer;
        readonly List<(uint pc, uint target, byte kind)> _seen = new List<(uint, uint, byte)>();

        [SetUp]
        public void SetUp()
        {
            _enabled = UdonProfilerStockHooks.Enabled;
            _observer = UdonProfilerStockHooks.Observer;
            _seen.Clear();
            if (!UdonProfilerStockHooks.Patched) Assert.Inconclusive("stock-VM transpiler not patched (layout or self-test failed, or StockHooks pref off); see the console warning");
        }

        [TearDown]
        public void TearDown()
        {
            UdonProfilerStockHooks.Enabled = _enabled;
            UdonProfilerStockHooks.Observer = _observer;
        }

        static (uint result, int r) Run(string source, bool? condition = null)
        {
            var program = MapAsm.Editor.Assemble(source);
            var vm = MapAsm.Editor.ConstructUdonVM();
            Assert.IsTrue(vm.LoadProgram(program));
            if (condition.HasValue) vm.InspectHeap().SetHeapVariable(program.SymbolTable.GetAddressFromSymbol("c"), condition.Value);
            vm.SetProgramCounter(program.EntryPoints.GetAddressFromSymbol("_start"));
            uint result = vm.Interpret();
            int r = program.SymbolTable.TryGetAddressFromSymbol("r", out uint slot) ? vm.InspectHeap().GetHeapVariable<int>(slot) : 0;
            return (result, r);
        }

        void Observe()
        {
            UdonProfilerStockHooks.Observer = (pc, target, kind) => _seen.Add((pc, target, kind));
            UdonProfilerStockHooks.Enabled = true;
        }

        [Test]
        public void Patched() => Assert.IsTrue(UdonProfilerStockHooks.Patched);

        [Test]
        public void CallReturnObserved()
        {
            Observe();
            var hooked = Run(CallReturn);
            UdonProfilerStockHooks.Enabled = false;
            var expected = new List<(uint, uint, byte)> { (8u, 40u, 0), (84u, 16u, 1), (24u, 40u, 0), (84u, 32u, 1), (32u, 0xFFFFFFFCu, 0) };
            CollectionAssert.AreEqual(expected, _seen);

            _seen.Clear();
            var plain = Run(CallReturn);
            Assert.AreEqual(0, _seen.Count);
            Assert.AreEqual(plain.result, hooked.result);
            Assert.AreEqual(plain.r, hooked.r);
            Assert.AreEqual(6, hooked.r);
        }

        [Test]
        public void JumpIfFalseObservedOnlyWhenTaken()
        {
            Observe();
            Run(Jif, false);
            var jif = _seen.FindAll(x => x.kind == 2);
            Assert.AreEqual(1, jif.Count);
            Assert.AreEqual(8u, jif[0].pc);
            Assert.AreEqual(20u, jif[0].target);

            _seen.Clear();
            Run(Jif, true);
            Assert.AreEqual(0, _seen.FindAll(x => x.kind == 2).Count);
        }
    }
}
