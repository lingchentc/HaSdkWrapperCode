using HaSdkWrapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SdkTest
{
    [TestClass]
    public class UnitTest1
    {
        [TestMethod]
        public void TestMethod1()
        {
            HaCamera.InitEnvironment();
            HaCamera camera = new HaCamera() 
            {
                 
            };

            camera.AlarmRequestReceived += Camera_AlarmRequestReceived;
        }

        private void Camera_AlarmRequestReceived(object sender, AlarmRequestEventArgs e)
        {
            throw new System.NotImplementedException();
        }
    }
}
