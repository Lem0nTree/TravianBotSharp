namespace MainCore.Errors
{
    public class Stop : Error
    {
        private Stop() : this("Bot must stop")
        {
        }

        protected Stop(string message) : base(message) { }

        public static Result Error => new Stop();
        public static Result DriverNotReady => Error.WithError("Driver is not ready.");
    }
}
