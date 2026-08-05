namespace SelfReferenceFixture;

// IControlStub is only referenced by ImageMgr (in its base list). Once ImageMgr
// is pruned, IControlStub becomes unused and must cascade-prune in a later pass.
public interface IControlStub<T> { }

// ImageMgr names itself in its own type arguments. That self-reference must NOT
// count as external usage — nothing outside this declaration references
// ImageMgr, so it must be pruned.
public class ImageMgr : IControlStub<ImageMgr> { }

// A type that references itself in its base list (CRTP-style). Same rule: the
// self-reference in `Derived : Base<Derived>` is not external usage.
public class Base<T> { }
public class Derived : Base<Derived> { }

// UsedType is referenced from Program.Main and must be kept.
public class UsedType
{
    public void DoWork() { }
}
