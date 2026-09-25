namespace EEWTelop.Wpf;

public static class BuildFeatures
{
    public static bool ExtendedFeaturesEnabled
    {
        get
        {
#if QTELOPPER_EXTENDED_FEATURES
            return true;
#else
            return false;
#endif
        }
    }

    public static bool AxisProviderEnabled
    {
        get
        {
#if QTELOPPER_AXIS_PROVIDER
            return true;
#else
            return false;
#endif
        }
    }

    public static bool DmdataProviderEnabled
    {
        get
        {
#if QTELOPPER_DMDATA_PROVIDER
            return true;
#else
            return false;
#endif
        }
    }
}
