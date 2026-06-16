namespace Dasher.Windows.Engine;

public static class ParameterKeys
{
    public static readonly int SP_ALPHABET_ID = NativeBridge.dasher_find_parameter_key("SP_ALPHABET_ID");
    public static readonly int SP_COLOUR_ID = NativeBridge.dasher_find_parameter_key("SP_COLOUR_ID");
    public static readonly int SP_INPUT_FILTER = NativeBridge.dasher_find_parameter_key("SP_INPUT_FILTER");
    public static readonly int SP_INPUT_DEVICE = NativeBridge.dasher_find_parameter_key("SP_INPUT_DEVICE");
    public static readonly int BP_AUTO_SPEEDCONTROL = NativeBridge.dasher_find_parameter_key("BP_AUTO_SPEEDCONTROL");
    public static readonly int BP_LM_ADAPTIVE = NativeBridge.dasher_find_parameter_key("BP_LM_ADAPTIVE");
    public static readonly int BP_SPEAK_ALL_ON_STOP = NativeBridge.dasher_find_parameter_key("BP_SPEAK_ALL_ON_STOP");
    public static readonly int BP_SPEAK_WORDS = NativeBridge.dasher_find_parameter_key("BP_SPEAK_WORDS");
}
