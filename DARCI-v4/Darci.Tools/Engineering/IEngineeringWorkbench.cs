namespace Darci.Tools.Engineering;

public interface IEngineeringWorkbench
{
    Task<EngineeringWorkbenchResult> Run(EngineeringWorkRequest request);
}
