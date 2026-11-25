using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class RefitOverrideCheck
{
    internal static void Run(Assembly plugin)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        var type=plugin.GetType("Aetherform.EquipRefits",true)!;
        var instance=RuntimeHelpers.GetUninitializedObject(type);
        var field=type.GetField("collectionSettings",flags)!;
        var dictionary=(IDictionary)Activator.CreateInstance(field.FieldType)!;
        var tuple=field.FieldType.GetGenericArguments()[1];
        const string directory="AF Vanilla Fit override-fixture";
        field.SetValue(instance,dictionary);
        type.GetField("priority",flags)!.SetValue(instance,-1);
        var apply=type.GetMethod("ApplySettings",flags)!;
        foreach(bool enabled in new[]{false,true})
        {
            var options=new Dictionary<string,List<string>>{{"Fit",["My custom fit"]}};
            dictionary[directory]=Activator.CreateInstance(tuple,[enabled,0,options,false,false])!;
            foreach(string? desired in new string?[]{null,"New automatic fit"})
            foreach(int? requested in new int?[]{null,0,5})
            {
                // No plugin interface is supplied. Reaching any IPC mutation
                // would throw: a protected override must exit before that.
                if((bool)apply.Invoke(instance,[Guid.Empty,directory,desired,requested])!)
                    throw new Exception("Automatic sync changed a protected override");
                var state=dictionary[directory]!;
                if(Convert.ToBoolean(tuple.GetField("Item1")!.GetValue(state))!=enabled ||
                    Convert.ToInt32(tuple.GetField("Item2")!.GetValue(state))!=0 || options["Fit"].Single()!="My custom fit")
                    throw new Exception("Override enable state, priority or selected option changed");
            }
        }
        Console.WriteLine("Actual settings sync preserves enabled and disabled manual overrides without reaching IPC.");
    }
}
