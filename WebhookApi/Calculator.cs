namespace WebhookAPi;
public class Calculator
{
    public int AddNumbers(int a, int b)
    {
        return checked(a + b);
    }
}