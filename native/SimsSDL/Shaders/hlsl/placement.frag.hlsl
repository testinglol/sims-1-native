static float4 color;
static float4 inColor;

struct SPIRV_Cross_Input
{
    float4 inColor : TEXCOORD0;
};

struct SPIRV_Cross_Output
{
    float4 color : SV_Target0;
};

void frag_main()
{
    color = inColor;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    inColor = stage_input.inColor;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
