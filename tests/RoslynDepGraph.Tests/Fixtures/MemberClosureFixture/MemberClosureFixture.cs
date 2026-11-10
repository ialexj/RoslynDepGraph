class Entry
{
    public void Call(Service service) => service.Used();

    public Leaf Read(Service service) => service.Value;

    public Service Create() => new Service();

    public Service TypeOnly(Service service) => service;
}

class Service
{
    public Service() => Used();

    public Leaf Value => Tail.Result;

    public void Used() => Tail.Finish();

    public void Unused() => Noise.Explode();
}

class Tail
{
    public static Leaf Result = new Leaf();

    public static void Finish() { }
}

class Leaf { }

class Noise
{
    public static void Explode() { }
}
