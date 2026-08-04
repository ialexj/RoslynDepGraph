// System-reference fixture for --include-system tests.
// Holder has a string field (a framework/SpecialType). With IncludeSystem=false
// (default) the string edge is suppressed; with IncludeSystem=true it surfaces as
// an external (IsExternal=true) edge.

class Holder
{
    string name = "";

    string GetName()
    {
        return name;
    }
}
