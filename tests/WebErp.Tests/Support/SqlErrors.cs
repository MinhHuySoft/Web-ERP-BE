using System.Reflection;
using Microsoft.Data.SqlClient;

namespace WebErp.Tests.Support;

/// <summary>Tạo <see cref="SqlException"/> với mã lỗi cho trước (SqlClient không có constructor công khai).</summary>
public static class SqlErrors
{
    public static SqlException Create(int number, byte errorClass = 11)
    {
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var ctor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).First();
        var firstInt = true;
        var bytes = 0;
        var args = ctor.GetParameters().Select(p =>
        {
            if (p.ParameterType == typeof(int))
            {
                var v = firstInt ? number : 0;
                firstInt = false;
                return (object?)v;
            }
            if (p.ParameterType == typeof(byte)) return (object?)(bytes++ == 1 ? errorClass : (byte)0); // (state, class)
            if (p.ParameterType == typeof(uint)) return 0u;
            if (p.ParameterType == typeof(string)) return "test";
            return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }).ToArray();
        var error = (SqlError)ctor.Invoke(args);
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(collection, [error]);
        var create = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == "CreateException" && m.GetParameters()[0].ParameterType == typeof(SqlErrorCollection))
            .OrderBy(m => m.GetParameters().Length).First();
        var createArgs = create.GetParameters().Select((p, i) => i switch
        {
            0 => collection,
            1 when p.ParameterType == typeof(string) => "13.0.1601",
            _ => p.HasDefaultValue ? p.DefaultValue : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        }).ToArray();
        return (SqlException)create.Invoke(null, createArgs)!;
    }
}
