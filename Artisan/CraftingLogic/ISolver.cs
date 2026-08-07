using System.Collections.Generic;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Artisan.CraftingLogic;

// solver definition describes a family of solvers; it is used to create individual solvers for specific crafts
public interface ISolverDefinition
{
    public record struct Desc(ISolverDefinition Def, int Flavour, int Priority, string Name, string UnsupportedReason = "")
    {
        public Solver? CreateSolver(CraftState craft)
        {
            return this == default ? null : Def.Create(craft, Flavour);
        }
    }

    public IEnumerable<Desc> Flavours(CraftState craft);
    public Solver Create(CraftState craft, int flavour);
}

// base class for solvers; instances of solvers can be stateful, so be sure to clone if you want to do some simulation without disturbing original state
public abstract class Solver
{
    public record struct Recommendation(Skills Action, string Comment = "");

    public virtual Solver Clone() => (Solver)MemberwiseClone(); // shallow copy by default
    public abstract Recommendation Solve(CraftState craft, StepState step); // note that this function potentially mutates state!
}

public interface ICraftValidator
{
    public bool Validate(CraftState craft);
}

// a simple wrapper around solver that allows creating clones on-demand, but does not allow calling solve directly
public struct SolverRef
{
    public string Name { get; private init; } = "";
    private Solver? _solver;

    public SolverRef(string name, Solver? solver = null)
    {
        Name = name;
        _solver = solver;
    }

    public Solver? Clone() => _solver?.Clone();
    // note: look through the wrappers - callers care about what is actually driving the craft,
    // and a raphael macro wrapped for condition awareness / material miracle is still a macro.
    // ⚠️ 每加一層包裝就要在這裡加一層,漏了的失敗形式是靜默的:IsType<MacroSolver>() 回 false,
    //    呼叫端(CraftingWindow 的祕籍提示)會安靜地走到另一條分支。
    // 🔴 保留 `_solver is T` 這一項:拿掉的話 IsType<OpportunisticSolver>() 之類「問的就是包裝本身」
    //    的呼叫會從 true 變 false,那是回退既有行為。
    public bool IsType<T>() where T : Solver => _solver is T || Unwrap(_solver) is T;

    private static Solver? Unwrap(Solver? s) => s switch
    {
        Solvers.MaterialMiracleSolver m => Unwrap(m.Inner),
        Solvers.OpportunisticSolver o => Unwrap(o.Inner),
        _ => s,
    };

    public static implicit operator bool(SolverRef x) => x._solver != null;
}
