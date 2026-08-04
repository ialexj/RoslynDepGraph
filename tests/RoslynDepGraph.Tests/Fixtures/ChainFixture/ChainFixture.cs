// Chain fixture for transitive-closure tests: C -> B -> A.
// C references B (field), B references A (field). With --transitive, C's closure
// should include A as a transitive (Direct=false) edge.

class A
{
    public void DoA() { }
}

class B
{
    A a;

    void UseA()
    {
        a.DoA();
    }
}

class C
{
    B b;

    void UseB()
    {
        // Touches B only directly; A is only reachable transitively via B.
        b.GetType();
    }
}
