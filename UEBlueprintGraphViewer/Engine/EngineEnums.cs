namespace UEBlueprintGraphViewer.Engine
{
    public class EngineEnums
    {
        public enum EEdGraphPinDirection
        {
            EGPD_Input,
            EGPD_Output,
            EGPD_MAX,
        }

        public enum EPinContainerType : byte
        {
            None,
            Array,
            Set,
            Map
        };

        // Mirrors EBlueprintType in Engine/Classes/Engine/Blueprint.h
        public enum EBlueprintType : byte
        {
            Normal = 0,
            Const = 1,
            MacroLibrary = 2,
            Interface = 3,
            LevelScript = 4,
            FunctionLibrary = 5,
        }
    }
}
