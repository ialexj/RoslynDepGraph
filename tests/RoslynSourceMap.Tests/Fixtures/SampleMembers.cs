// Sample fixture for RoslynSourceMap: exercises classes, nested types, methods,
// properties, fields, a delegate, a record, and leading comments.

namespace SampleApp
{
    // The main generated form.
    public class AutoForm
    {
        // Generated from: Optiware/Auto.ctl:61
        private void Command1_Click()
        {
            // ...body...
        }

        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        public string Title { get; set; }

        /* A multi-line
           comment. */
        private int _count;

        public int Count, Total;

        public event EventHandler? Changed;

        public delegate void Handler(int value);

        public class Nested
        {
            public void Run() { }
        }
    }

    public record Point(int X, int Y);
}
