namespace FilterFixture;

// Both types are unused. With --types FilterFixture.KeepMe, only KeepMe is a
// candidate; DropMe is not pruned.
public class KeepMe
{
    public void DoWork() { }
}

public class DropMe
{
    public void DoWork() { }
}
