namespace BGAReflowStudio.Models;

/// <summary>
/// Auto-mode bottom-heater controller (gains Kp=0.85, Ki=4.67, Kd=0.15).
/// Produces a control value Cv in [0,100] that the UI maps to a 0..9 bottom-power band.
/// </summary>
public sealed class PidRegulator
{
    public float Kp = 0.85f;
    public float Ki = 4.67f;
    public float Kd = 0.15f;
    public float ManualValue = 40f;

    private float _sumError;
    private float _initialError;
    private float _lastPv;
    private float _lastSp;
    private int _firstExecution;

    // Timer4 helper state (x/y/vv ladder from the original).
    private float _x, _y, _vv;

    public void Reset()
    {
        _sumError = 0f;
        _initialError = 0f;
        _lastPv = 0f;
        _lastSp = 0f;
        _firstExecution = 0;
        _x = _y = _vv = 0f;
    }

    /// <param name="setPoint">controller set point</param>
    /// <param name="autoBottom">autobottom offset (NUD33)</param>
    /// <param name="pv2">bottom process temperature (P2)</param>
    /// <param name="topTemp">top process temperature (PV / pomiar_t)</param>
    /// <param name="bottomContinue">force-100% threshold from the reference curve</param>
    /// <returns>control value 0..100</returns>
    public float Step(int setPoint, decimal autoBottom, int pv2, int topTemp, int bottomContinue)
    {
        float sp = setPoint + (float)autoBottom;
        float pv = pv2;

        // Timer4 integral-boost ladder (vv) replicated from the original.
        if (pv >= sp) { _x = pv; _y = 0f; _vv = 0f; }
        else if (_x > pv) { _x = pv; }

        if (_x == pv)
        {
            _y++;
            if (_y == 100f)
            {
                _vv++;
                _y = 0f;
            }
        }

        if (pv > _x && pv < sp) { _x = pv; _y = 0f; }

        return Regulate(sp, pv, topTemp, bottomContinue);
    }

    private float Regulate(float sp, float pv, int topTemp, int bottomContinue)
    {
        float errors = sp - pv;
        _sumError += errors;
        float iterm = Ki * _sumError;
        float pterm, dterm;

        if (_firstExecution < 2)
        {
            if (_firstExecution == 0)
            {
                _sumError = ManualValue / Ki;
                _firstExecution = 1;
                _initialError = errors;
            }

            pterm = 0f;
            dterm = 0f;
            if (_initialError > 0f && errors < 0f) { _firstExecution = 2; _lastPv = pv; }
            if (_initialError < 0f && errors > 0f) { _firstExecution = 2; _lastPv = pv; }
            _lastSp = sp;
        }
        else
        {
            float dPv = _lastPv - pv;
            _lastPv = pv;
            dterm = Kd * dPv;
            if (sp == _lastSp)
            {
                pterm = Kp * errors;
            }
            else
            {
                pterm = 0f;
                dterm = 0f;
                if (sp > _lastSp && pv > sp) { _lastSp = sp; _lastPv = pv; }
                if (sp < _lastSp && pv < sp) { _lastSp = sp; _lastPv = pv; }
            }
        }

        float cv = pterm + iterm + dterm + _vv;
        if (cv > 100f) cv = 100f;
        if (cv < 0f) cv = 0f;
        if (topTemp > bottomContinue) cv = 100f;

        _sumError = 0f; // matches the original (it zeroes sum_error each pass)
        return cv;
    }

    /// <summary>Maps a 0..100 control value to the bottom-power combo index 0..9.</summary>
    public static int CvToPowerIndex(float cv) => cv switch
    {
        <= 15f => 0,
        <= 25f => 1,
        <= 35f => 2,
        <= 45f => 3,
        <= 55f => 4,
        <= 65f => 5,
        <= 75f => 6,
        <= 85f => 7,
        <= 95f => 8,
        _ => 9,
    };
}
