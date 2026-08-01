// Sample fixture for RoslynDepGraph, matching the spec in docs/spec.md.

class A
{
    public void DoSomething() { }
}

class B
{
    A member;

    void M()
    {
        member.DoSomething();
    }

    void V() { }
}
