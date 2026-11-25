namespace Aetherform.Geometry;

internal static class FamilyRefitOrder
{
    // Exact correspondence is the preferred route. Movement chooses the size
    // within that route; UV correspondence remains available as a fallback.
    internal static bool Prefer(bool direct,float movement,float guide,bool currentDirect,float currentMovement,float currentGuide)
    {
        if(direct!=currentDirect) return direct;
        return movement<currentMovement-1e-6f ||
            Math.Abs(movement-currentMovement)<=1e-6f && guide<currentGuide-1e-6f;
    }
}
