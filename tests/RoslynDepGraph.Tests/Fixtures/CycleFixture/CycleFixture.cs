// Cycle fixture for transitive-closure cycle-safety tests: A <-> B.
// A references B, B references A. The closure must terminate (visited set) and
// must not duplicate edges. Note: in a cycle, the closure produces a transitive
// self-edge (A -> A with Direct=false) because the direct self-edge drop only
// applies to the symbol currently being expanded, not the report node.

class A
{
    B b;

    void UseB()
    {
        b.UseA();
    }
}

class B
{
    A a;

    void UseA()
    {
        a.UseB();
    }
}
