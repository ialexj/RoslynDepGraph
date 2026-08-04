// Sample fixture for RoslynSourceMap, matching the request's example shape:
// a class whose method carries a "Generated from" leading comment.

namespace Optiware
{
    public class Auto
    {
        // Generated from: Optiware/Auto.ctl:61
        private void Command1_Click()
        {
            // ...
        }

        public string? Title { get; set; }
    }
}
