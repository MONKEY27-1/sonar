namespace Soundboard.Helpers;

public static class EnumBindingSource
{
    public static Array GetValues<T>() where T : struct, Enum => Enum.GetValues<T>();
}
