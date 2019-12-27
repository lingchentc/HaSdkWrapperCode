﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Reflection;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Globalization;

namespace HaSdkWrapper
{
    public class HaCamera
    {

        #region callback defines
        private HA_FaceRecoCb_t _faceRecoCallback;
        private HA_ConnectEventCb_t _connectEventCallback;
        private static discover_ipscan_cb_t _discoverIpscanCallback;
        private HA_ReadTSerialCb_t _serialDataCallback;
        private HA_FaceQueryCb_t _faceQueryCallback;
        private HA_AlarmRecordCb_t _alarmRecordCallback;
        private HA_AlarmRequestCb_t _alarmRequestCallback;
        private HA_FaceRecordCb_t _faceRecordQueryCallback;
        private HA_SnapshotCb_t _snapshotCallback;
        #endregion callback defines

        #region events
        /// <summary>
        /// 连接状态发生变化
        /// </summary>
        public event EventHandler<ConnectEventArgs> ConnectStateChanged;
        /// <summary>
        /// 收到人脸抓拍触发的事件
        /// </summary>
        public event EventHandler<FaceCapturedEventArgs> FaceCaptured;
        /// <summary>
        /// 搜索设备得到一条结果时触发的事件
        /// </summary>
        public static event EventHandler<DeviceDiscoverdEventArgs> DeviceDiscovered;
        /// <summary>
        /// 收到串口数据时的事件
        /// </summary>
        public event EventHandler<SerialDataArrivedEventArgs> SerialDataArrived;
        /// <summary>
        /// 收到设备请求开闸请求时产生的事件
        /// </summary>
        public event EventHandler<AlarmRequestEventArgs> AlarmRequestReceived;
        /// <summary>
        /// 收到设备开闸记录时产生的事件
        /// </summary>
        public event EventHandler<AlarmRecordEventArgs> AlarmRecordReceived;
        #endregion events

        #region properties
        /// <summary>
        /// 相机ip
        /// </summary>
        public string Ip { get; set; }


        /// <summary>
        /// 相机的端口号，缺省为9527
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// 登录用户名
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// 登录密码
        /// </summary>
        public string Password { get; set; }
        /// <summary>
        /// 连接状态
        /// </summary>
        public bool Connected { get { return NativeMethods.HA_Connected(_cam); } }
        private IntPtr _cam = IntPtr.Zero;
        [ThreadStatic]
        private int lastErrorCode;
        private List<FaceEntity> _queriedFaces = new List<FaceEntity>();
        private volatile int _faceCount = 0;
        private Semaphore _queryFacesPageSemaphore = new Semaphore(0, 1);
        private List<RecordDataEntity> _queriedRecords = new List<RecordDataEntity>();
        private volatile int _recordCount = 0;
        private Semaphore _queryRecordPageSemaphore = new Semaphore(0, 1);
        private Semaphore _snapshotSemaphore = new Semaphore(0, 1);
        private Image _snapshotImage = null;
        #endregion properties

        #region methods
        /// <summary>
        /// 新版的sdk底层自行管理内存，无需设置预期连接数
        /// </summary>
        public static void InitEnvironment()
        {
            InitEnvironment(9);
        }
        /// <summary>
        /// <para>初始化SDK底层库，在进程中应该只需要调用一次</para>
        /// <para>除非是需要在不关闭进程的情况下更改连接数或者清理内存</para>
        /// <para>如果需要更改连接数或者清理内存，则需要先DeInit之后再行Init</para>
        /// </summary>
        /// <param name="maxConnectNum">需要最大连接的设备数量；请尽量设置小额数量，因为程序会为每一个预期的连接立即分配内存</param>
        public static void InitEnvironment(uint maxConnectNum)
        {
            if (maxConnectNum < 10)
                NativeMethods.HA_Init();
            else
                NativeMethods.HA_InitEx(maxConnectNum);
            NativeMethods.HA_SetCharEncode(CHAR_ENCODE.CHAR_ENCODE_UTF8);
            NativeMethods.HA_SetNotifyConnected(1);
            NativeMethods.HA_InitFaceModel(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "face.dat"));

            _discoverIpscanCallback = DiscoverIpCb;
            NativeMethods.HA_RegDiscoverIpscanCb(_discoverIpscanCallback, 0);
        }
        /// <summary>
        /// 卸载SDK底层库，理论上无需调用此函数，可待其随进程自行退出
        /// </summary>
        public static void DeInitEnvironment()
        {
            NativeMethods.HA_DeInit();
        }
        /// <summary>
        /// <para>获取SDK版本</para>
        /// <para>这个值不做参考</para>
        /// </summary>
        /// <returns>SDK版本</returns>
        [Obsolete("不要调用这个函数，会崩溃")]
        public static int GetEnvironmentVersion()
        {
            return NativeMethods.HA_GetVersion();
        }
        /// <summary>
        /// <para>设置sdk二级密码</para>
        /// <para>sdk内部会用这个密码去连接设备</para>
        /// </summary>
        /// <param name="password">二级密码</param>
        /// <returns>是否设置成功</returns>
        public static bool SetSDKPazzword(string password)
        {
            return NativeMethods.HA_SetSDKPassword(password) == 0;
        }

        /// <summary>
        /// <para>搜索设备</para>
        /// <para>需要通过DeviceDiscovered事件接收发现的设备</para>
        /// </summary>
        public static void DiscoverDevice()
        {
            NativeMethods.HA_DiscoverIpscan();
        }

        /// <summary>
        /// <para>校验图片是否可用于模板下发</para>
        /// <para>即判定图片中是否有清晰正面人脸</para>
        /// </summary>
        /// <param name="imgData">图片数据(使用File.RadAllBytes直接读取文件内容传入)</param>
        /// <returns>图片是否可下发</returns>
        public static bool ValidImage(byte[] imgData)
        {
            IntPtr data = Marshal.AllocHGlobal(imgData.Length);
            Marshal.Copy(imgData, 0, data, imgData.Length);
            IntPtr twist = Marshal.AllocHGlobal(150 * 150 * 3);
            int a = 0, b = 0, c = 0;
            int ret = NativeMethods.HA_GetJpgFaceTwist(data, imgData.Length, twist, ref a, ref b, ref c);
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(twist);
            return ret == 0;
        }

        /// <summary>
        /// 从图片生成缩略图和归一化图（用于用于纯协议对接）
        /// </summary>
        /// <param name="image">原始图片数据（通过File.ReadAllBytes读出来的）</param>
        /// <returns>如果检测到人脸且符合要求，则返回两个元素的数组，第一个元素是缩略图，第二个元素是归一化图，如果使用协议对接，则直接将其封入数据包即可（http协议的话就直接转成base64放入特定位置即可）；如果检测人脸失败或人脸不符合要求，则返回null</returns>
        public static byte[][] TwistImage(byte[] image)
        {
            byte[][] ret = null;
            if (image == null) return ret;
            byte[] twist_image = new byte[1024 * 1024];
            byte[] thumb_image = new byte[1024 * 1024];
            int twist_size = twist_image.Length;
            int thumb_size = thumb_image.Length;
            int twist_w = 0;
            int twist_h = 0;
            int _ret = NativeMethods.HA_GetJpgFeatureImage(image, image.Length, twist_image, ref twist_size, ref twist_w, ref twist_h, thumb_image, ref thumb_size);
            if (_ret != 0)
            { return ret; }
            ret = new byte[2][];
            ret[1] = new byte[twist_size];
            Array.Copy(twist_image, ret[1], twist_size);
            ret[0] = new byte[thumb_size];
            Array.Copy(thumb_image, ret[0], thumb_size);
            return ret;
        }
        /// <summary>
        /// 设置注册时人脸质量校验开关
        /// </summary>
        /// <param name="checkFaceQuality">是否开启人脸质量校验</param>
        public static void SetFaceCheckEnable(bool checkFaceQuality)
        {
            NativeMethods.HA_SetFaceCheckEnable(checkFaceQuality ? 1 : 0);
        }

        public HaCamera()
        {
            Port = 9527;
            Username = "";
            Password = "";
        }

        /// <summary>
        /// 获取上次错误码
        /// </summary>
        /// <returns>上次操作的错误码</returns>
        public int GetLastError()
        {
            return lastErrorCode;
        }
        /// <summary>
        /// 获取错误码对应文字描述
        /// </summary>
        /// <param name="errorCode">错误码</param>
        /// <returns>文字描述</returns>
        public static string GetErrorDescribe(int errorCode)
        {
            switch (errorCode)
            {
                case NativeConstants.ERR_NONE:
                    return "操作成功";
                case NativeConstants.ERR_INVALID_PARAM:
                    return "参数非法（缺失或错误的参数）";
                case NativeConstants.ERR_TIMEOUT:
                    return "操作超时（设备在规定时间内未响应当前操作）";
                case NativeConstants.ERR_SYS_NOT_MATCH:
                    return "系统不匹配（操作设备不是人脸抓拍相机）";
                case NativeConstants.ERR_UNCONNECTED:
                    return "未连接（网络问题）";
                case NativeConstants.ERR_NOT_IMPLEMENTED:
                    return "未实现（设备版本较低不支持当前操作）";
                case NativeConstants.ERR_GET_FACE_FEATURE:
                    return "提取人脸特征失败（须保证图像中有且仅有一张人脸）";
                case NativeConstants.ERR_FACE_ID_REPEAT:
                    return "添加人员失败（人员id重复）";
                case NativeConstants.ERR_FACE_ID_NOT_EXITS:
                    return "修改人脸特征失败（未找到对应ID）";
                case NativeConstants.ERR_GET_FACE_INIT:
                    return "人脸提取器未初始化（请调用HA_InitFaceModel初始化）";
                case NativeConstants.ERR_GET_FACE_ID:
                    return "查询人脸失败（无当前记录）";
                case NativeConstants.ERR_SERIAL_INDEX:
                    return "错误的串口编号（目前只支持HA_SERIAL_RS485和HA_SERIAL_RS232）";
                case NativeConstants.ERR_SYSTEM_REBOOT:
                    return "重启系统失败";
                case NativeConstants.ERR_APP_REBOOT:
                    return "重启应用程序失败";
                case NativeConstants.ERR_ENCODE_JPG:
                    return "压缩图像失败";
                case NativeConstants.ERR_FACES_NUM:
                    return "单个人员最大支持5张图像";
                case NativeConstants.ERR_IMAGE_DECODE:
                    return "图像解码失败";
                case NativeConstants.ERR_IMAGE_SIZE:
                    return "图像太大，用于提取特征的jpg图像不能超过10M，下发到相机的jpg图像 宽高不能超过128x128";
                case NativeConstants.ERR_IMAGE_PATH:
                    return "文件不存在，请检查图片路径是否正确";
                case NativeConstants.ERR_SAVE_IMG_NUM:
                    return "目前相机只支持存一张模板图像，pictureFlags必须<=1";
                case NativeConstants.ERR_PTZ_CTRL:
                    return "不支持的云台控制，目前只支持5：变倍（放大） 6：变倍（缩小） 7：聚焦（拉近） 8：聚焦（拉远）";
                case NativeConstants.ERR_PTZ_CTRL_MODE:
                    return "不支持的控制模式，1：一次运动 2：启动 3：停止";
                case NativeConstants.ERR_UPPER_LIMIT:
                    return "人员数量已达上限";
                case NativeConstants.ERR_PROTOCAL_VER:
                    return "协议版本不匹配，请确认SDK和相机固件版本";
                case NativeConstants.ERR_REQUEST_CMD:
                    return "不支持的操作请求";
                case NativeConstants.ERR_PACKET_DATA:
                    return "请求数据包包含不合法字段";
                case NativeConstants.ERR_AUTH_FAILED:
                    return "认证失败";
                case NativeConstants.ERR_WRITE_DATA:
                    return "写入数据失败";
                case NativeConstants.ERR_READ_DATA:
                    return "读取数据失败";
                case NativeConstants.ERR_TWIST_FACE:
                    return "归一化图像失败";
                case NativeConstants.ERR_EXTRACT_FEATURE:
                    return "提取特征失败";
                case NativeConstants.ERR_MIN_FACE:
                    return "人脸尺寸太小，人脸轮廓必须大于96*96";
                case NativeConstants.ERR_QVALUE_TOO_SMALL:
                    return "人像质量太差不满足注册条件";
                case NativeConstants.ERR_INVALID_SYNC_MODE:
                    return "无效的同步操作";
                case NativeConstants.ERR_WG_QUERY_MODE:
                    return "韦根卡号不支持模糊查询";
                case NativeConstants.ERR_SYSTEM_BUSY:
                    return "操作系统繁忙";
                case NativeConstants.ERR_VERSION_MATCH:
                    return "版本不匹配";
                case NativeConstants.ERR_TOO_MUCH_FACE:
                    return "图像中人脸数不为1";
                case NativeConstants.ERR_FACE_INCOMPLETE:
                    return "图像中人脸不完整";
                case NativeConstants.ERR_ANGLE_PITCH:
                    return "人脸俯仰角太大";
                case NativeConstants.ERR_ANGLE_YAW:
                    return "人脸侧偏角太大";
                case NativeConstants.ERR_ANGLE_ROLL:
                    return "人脸不正";
                case NativeConstants.ERR_MOUTH_OPEN:
                    return "张嘴幅度过大";
                case NativeConstants.ERR_YINYANG_FACE:
                    return "光照不均匀";
                case NativeConstants.ERR_VISIBLE_TARGET:
                    return "未检出指定个数可见光目标";
                case NativeConstants.ERR_INFRARED_TARGET:
                    return "未检出指定个数红外光目标";
                case NativeConstants.ERR_ABERRATION_TOO_BIG:
                    return "可见光红外光像差过大";
                case NativeConstants.ERR_REPLYCODE_FEATURE_VERSION:
                    return "特征数据版本不匹配";
                case NativeConstants.ERR_LACK_TWISTIMGE:
                    return "缺少归一化图像";
                default:
                    return "未知";
            }
        }

        /// <summary>
        /// 连接相机（不播放视频）
        /// </summary>
        /// <returns>是否链接成功</returns>
        public bool Connect()
        {
            return Connect(default(IntPtr));
        }
        /// <summary>
        /// 连接相机
        /// </summary>
        /// <param name="hwnd">窗口的句柄，显示实时视频用，不需要显示视频的时候，传入default(IntPtr)</param>
        /// <returns>是否连接成功</returns>
        public bool Connect(IntPtr hwnd)
        {
            lastErrorCode = NativeConstants.ERR_NONE;

            //if (NativeMethods.HA_Connected(_cam))
            //{
            //    NativeMethods.HA_DisConnect(_cam);
            //}

            if (string.IsNullOrEmpty(Ip))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }

            if (Port == 0)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }

            int errorNum = -1;
            _cam = NativeMethods.HA_ConnectEx(Ip, (ushort)Port, Username, Password, ref errorNum, 0, true);
            if (_cam == IntPtr.Zero) return false;

            HookCallbackEx(_cam);

            if (hwnd != default(IntPtr))
                NativeMethods.HA_StartStreamEx(_cam, hwnd, null, 0);

            if (errorNum != 0)
            {
                lastErrorCode = errorNum;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 播放设备实时画面
        /// </summary>
        /// <param name="hwnd">需要显示的控件句柄</param>
        public void StartPlay(IntPtr hwnd)
        {
            NativeMethods.HA_StartStreamEx(_cam, hwnd, null, 0);
        }
        /// <summary>
        /// 停止设备实时画面预览
        /// </summary>
        public void StopPlay()
        {
            NativeMethods.HA_StopStream(_cam);
        }
        /// <summary>
        /// 软触发
        /// </summary>
        /// <returns>是否成功，如果失败需要排查错误</returns>
        public bool Trigger()
        {
            lastErrorCode = 0;
            int ret = NativeMethods.HA_Trigger(_cam, 0);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }

        /// <summary>
        /// 添加一张人脸模板到设备库中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="picPath">图片路径</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <returns>是否添加成功</returns>
        public bool AddFace(string personID, string personName, int personRole, string picPath, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (string.IsNullOrEmpty(picPath))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (!File.Exists(picPath))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            return AddFace(personID, personName, personRole, File.ReadAllBytes(picPath), wgNo, effectTime);
        }
        /// <summary>
        /// 添加一组图片到同一人员中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picPaths">图片路径（最多五张图片）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <returns>是否添加成功</returns>
        public bool AddFace(string personID, string personName, int personRole, string[] picPaths, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picPaths == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (picPaths.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var item in picPaths)
            {
                if (string.IsNullOrEmpty(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
                if (!File.Exists(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            byte[][] picDatas = new byte[picPaths.Length][];
            for (int i = 0; i < picPaths.Length; ++i)
            {
                picDatas[i] = File.ReadAllBytes(picPaths[i]);
            }
            return AddFace(personID, personName, personRole, picDatas, wgNo, effectTime);
        }
        /// <summary>
        /// 添加一张人脸模板到设备库中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="picData">图片数据（图片完整数据，通过File.ReadAllBytes读出来的）</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <returns>是否添加成功</returns>
        public bool AddFace(string personID, string personName, int personRole, byte[] picData, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picData == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (string.IsNullOrEmpty(personID))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (string.IsNullOrEmpty(personName))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (personRole != 0 && personRole != 1 && personRole != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            FaceFlags ff = new FaceFlags();
            ff.wgCardNO = wgNo;
            ff.effectTime = effectTime;
            ff.faceID = Converter.ConvertStringToUTF8(personID, 20);
            ff.faceName = Converter.ConvertStringToUTF8(personName, 16);
            ff.role = personRole;
            IntPtr picDataPtr = Marshal.AllocHGlobal(picData.Length);
            Marshal.Copy(picData, 0, picDataPtr, picData.Length);
            int ret = NativeMethods.HA_AddJpgFace(_cam, ref ff, picDataPtr, picData.Length);
            Marshal.FreeHGlobal(picDataPtr);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 添加一组图片到同一人员中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picDatas">图片数据（最多五张图片，需要图片完整数据，通过File.ReadAllBytes读出来的）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <returns>是否添加成功</returns>
        public bool AddFace(string personID, string personName, int personRole, byte[][] picDatas, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picDatas == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (picDatas.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var item in picDatas)
            {
                if (item == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            if (string.IsNullOrEmpty(personID))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (string.IsNullOrEmpty(personName))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (personRole != 0 && personRole != 1 && personRole != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            FaceFlags ff = new FaceFlags();
            ff.wgCardNO = wgNo;
            ff.effectTime = effectTime;
            ff.faceID = Converter.ConvertStringToUTF8(personID, 20);
            ff.faceName = Converter.ConvertStringToUTF8(personName, 16);
            ff.role = personRole;
            FaceImage[] faceImages = new FaceImage[picDatas.Length];
            for (int i = 0; i < picDatas.Length; ++i)
            {
                faceImages[i].img_fmt = 0;
                faceImages[i].img_seq = i;
                byte[] imgData = picDatas[i];
                faceImages[i].img = Marshal.AllocHGlobal(imgData.Length);
                Marshal.Copy(imgData, 0, faceImages[i].img, imgData.Length);
                faceImages[i].img_len = imgData.Length;
            }
            int ret = NativeMethods.HA_AddJpgFaces(_cam, ref ff, faceImages, picDatas.Length, 1);
            if (ret != 0)
                lastErrorCode = ret;
            for (int i = 0; i < picDatas.Length; ++i)
            {
                Marshal.FreeHGlobal(faceImages[i].img);
            }
            return ret == 0;
        }
        /// <summary>
        /// <para>添加一组图片到同一人员中</para>
        /// <para>下发到多个设备</para>
        /// <para>如果参数错误，则不会将错误码设置到逐个设备而直接返回</para>
        /// <para>注意，此函数暂不支持多张图片选择性下发，如果发现有不合格的图片，则会全部不予下发</para>
        /// </summary>
        /// <param name="cameras">需要下发的设备列表</param>
        /// <param name="lastErrorCode">错误码，只在参数错误时有效，如果某个设备出错，则错误码需要像设备获取</param>
        /// <param name="wrongContinue">是否遇到错误后继续；此参数表示当队列中某个设备下发失败后是否继续进行后续设备的数据下发，如果此值设置为false，则不会为后续设备设置错误码</param>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picPaths">图片路径（最多五张图片）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <returns>是否添加成功(成功标识位与传入设备一致)</returns>
        public static bool[] AddFace(HaCamera[] cameras, ref int lastErrorCode, bool wrongContinue, string personID, string personName, int personRole, string[] picPaths, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picPaths == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[0];
            }
            if (picPaths.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[0];
            }
            foreach (var item in picPaths)
            {
                if (string.IsNullOrEmpty(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return new bool[0];
                }
                if (!File.Exists(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return new bool[0];
                }
            }
            byte[][] picDatas = new byte[picPaths.Length][];
            for (int i = 0; i < picPaths.Length; ++i)
            {
                picDatas[i] = File.ReadAllBytes(picPaths[i]);
            }
            return AddFace(cameras, ref lastErrorCode, wrongContinue, personID, personName, personRole, picDatas, wgNo, effectTime);
        }
        /// <summary>
        /// <para>添加一组图片到同一人员中</para>
        /// <para>下发到多个设备</para>
        /// <para>如果参数错误，则不会将错误码设置到逐个设备而直接返回</para>
        /// <para>注意，此函数暂不支持多张图片选择性下发，如果发现有不合格的图片，则会全部不予下发</para>
        /// </summary>
        /// <param name="cameras">需要下发的设备列表</param>
        /// <param name="lastErrorCode">错误码，只在参数错误时有效，如果某个设备出错，则错误码需要像设备获取</param>
        /// <param name="wrongContinue">是否遇到错误后继续；此参数表示当队列中某个设备下发失败后是否继续进行后续设备的数据下发，如果此值设置为false，则不会为后续设备设置错误码</param>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picDatas">图片数据（最多五张图片，需要图片完整数据，通过File.ReadAllBytes读出来的）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <returns>是否添加成功(成功标识位与传入设备一致)</returns>
        public static bool[] AddFace(HaCamera[] cameras, ref int lastErrorCode, bool wrongContinue, string personID, string personName, int personRole, byte[][] picDatas, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (cameras == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[0];
            }
            else if (cameras.Length == 0)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[0];
            }
            if (picDatas == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            if (picDatas.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            foreach (var item in picDatas)
            {
                if (item == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return new bool[cameras.Length];
                }
            }
            if (string.IsNullOrEmpty(personID))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            if (string.IsNullOrEmpty(personName))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            if (personRole != 0 && personRole != 1 && personRole != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            byte[][][] twistAndThumbs = new byte[picDatas.Length][][];
            int i = 0;
            foreach (byte[] picData in picDatas)
            {
                twistAndThumbs[i] = TwistImage(picData);
                if (twistAndThumbs[i++] == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return new bool[cameras.Length];
                }
            }
            FaceImage fi = new FaceImage();
            fi.img_fmt = 0;
            fi.img_seq = 0;
            fi.img = Marshal.AllocHGlobal(twistAndThumbs[0][0].Length);
            Marshal.Copy(twistAndThumbs[0][0], 0, fi.img, twistAndThumbs[0][0].Length);
            fi.img_len = twistAndThumbs[0][0].Length;
            FaceFlags ff = new FaceFlags();
            ff.wgCardNO = wgNo;
            ff.effectTime = effectTime;
            ff.faceID = Converter.ConvertStringToUTF8(personID, 20);
            ff.faceName = Converter.ConvertStringToUTF8(personName, 16);
            ff.role = personRole;
            int twistLen = 150 * 150 * 3;
            FaceImage[] twistImgs = new FaceImage[picDatas.Length];
            i = 0;
            foreach (byte[][] twistAndThumb in twistAndThumbs)
            {
                twistImgs[i] = new FaceImage();
                twistImgs[i].img = Marshal.AllocHGlobal(twistLen);
                Marshal.Copy(twistAndThumb[1], 0, twistImgs[i].img, twistLen);
                twistImgs[i].img_fmt = 1;
                twistImgs[i].img_seq = i;
                twistImgs[i].width = 150;
                twistImgs[i].height = 150;
                i++;
            }
            bool[] ret = new bool[cameras.Length];
            i = 0;
            foreach (HaCamera cam in cameras)
            {
                int _ret = NativeMethods.HA_AddFacePacket(cam._cam, ref ff, twistImgs, 0, picDatas.Length, ref fi, 1);
                if (_ret != 0)
                {
                    cam.lastErrorCode = _ret;
                    if (!wrongContinue)
                    {
                        Marshal.FreeHGlobal(fi.img);
                        foreach (var timg in twistImgs)
                        {
                            Marshal.FreeHGlobal(timg.img);
                        }
                        return ret;
                    }
                }
                else
                {
                    ret[i] = true;
                }
                i++;
            }
            Marshal.FreeHGlobal(fi.img);
            foreach (var timg in twistImgs)
            {
                Marshal.FreeHGlobal(timg.img);
            }
            return ret;
        }
        /// <summary>
        /// <para>更新一组图片到同一人员中</para>
        /// <para>下发到多个设备</para>
        /// <para>如果参数错误，则不会将错误码设置到逐个设备而直接返回</para>
        /// <para>注意，此函数暂不支持多张图片选择性下发，如果发现有不合格的图片，则会全部不予下发</para>
        /// </summary>
        /// <param name="cameras">需要下发的设备列表</param>
        /// <param name="lastErrorCode">错误码，只在参数错误时有效，如果某个设备出错，则错误码需要像设备获取</param>
        /// <param name="wrongContinue">是否遇到错误后继续；此参数表示当队列中某个设备下发失败后是否继续进行后续设备的数据下发，如果此值设置为false，则不会为后续设备设置错误码</param>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picPaths">图片路径（最多五张图片）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <returns>是否更新成功(成功标识位与传入设备一致)</returns>
        public static bool[] ModifyFace(HaCamera[] cameras, ref int lastErrorCode, bool wrongContinue, string personID, string personName, int personRole, string[] picPaths, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picPaths == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[0];
            }
            if (picPaths.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[0];
            }
            foreach (var item in picPaths)
            {
                if (string.IsNullOrEmpty(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return new bool[0];
                }
                if (!File.Exists(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return new bool[0];
                }
            }
            byte[][] picDatas = new byte[picPaths.Length][];
            for (int i = 0; i < picPaths.Length; ++i)
            {
                picDatas[i] = File.ReadAllBytes(picPaths[i]);
            }
            return ModifyFace(cameras, ref lastErrorCode, wrongContinue, personID, personName, personRole, picDatas, wgNo, effectTime);
        }
        /// <summary>
        /// <para>更新一组图片到同一人员中</para>
        /// <para>下发到多个设备</para>
        /// <para>如果参数错误，则不会将错误码设置到逐个设备而直接返回</para>
        /// <para>注意，此函数暂不支持多张图片选择性下发，如果发现有不合格的图片，则会全部不予下发</para>
        /// </summary>
        /// <param name="cameras">需要下发的设备列表</param>
        /// <param name="lastErrorCode">错误码，只在参数错误时有效，如果某个设备出错，则错误码需要像设备获取</param>
        /// <param name="wrongContinue">是否遇到错误后继续；此参数表示当队列中某个设备下发失败后是否继续进行后续设备的数据下发，如果此值设置为false，则不会为后续设备设置错误码</param>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picDatas">图片数据（最多五张图片，需要图片完整数据，通过File.ReadAllBytes读出来的）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <returns>是否更新成功(成功标识位与传入设备一致)</returns>
        public static bool[] ModifyFace(HaCamera[] cameras, ref int lastErrorCode, bool wrongContinue, string personID, string personName, int personRole, byte[][] picDatas, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (cameras == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[0];
            }
            else if (cameras.Length == 0)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[0];
            }
            if (picDatas == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            if (picDatas.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            foreach (var item in picDatas)
            {
                if (item == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return new bool[cameras.Length];
                }
            }
            if (string.IsNullOrEmpty(personID))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            if (string.IsNullOrEmpty(personName))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            if (personRole != 0 && personRole != 1 && personRole != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return new bool[cameras.Length];
            }
            byte[][][] twistAndThumbs = new byte[picDatas.Length][][];
            int i = 0;
            foreach (byte[] picData in picDatas)
            {
                twistAndThumbs[i] = TwistImage(picData);
                if (twistAndThumbs[i++] == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return new bool[cameras.Length];
                }
            }
            FaceImage fi = new FaceImage();
            fi.img_fmt = 0;
            fi.img_seq = 0;
            fi.img = Marshal.AllocHGlobal(twistAndThumbs[0][0].Length);
            Marshal.Copy(twistAndThumbs[0][0], 0, fi.img, twistAndThumbs[0][0].Length);
            fi.img_len = twistAndThumbs[0][0].Length;
            FaceFlags ff = new FaceFlags();
            ff.wgCardNO = wgNo;
            ff.effectTime = effectTime;
            ff.faceID = Converter.ConvertStringToUTF8(personID, 20);
            ff.faceName = Converter.ConvertStringToUTF8(personName, 16);
            ff.role = personRole;
            int twistLen = 150 * 150 * 3;
            FaceImage[] twistImgs = new FaceImage[picDatas.Length];
            i = 0;
            foreach (byte[][] twistAndThumb in twistAndThumbs)
            {
                twistImgs[i] = new FaceImage();
                twistImgs[i].img = Marshal.AllocHGlobal(twistLen);
                Marshal.Copy(twistAndThumb[1], 0, twistImgs[i].img, twistLen);
                twistImgs[i].img_fmt = 1;
                twistImgs[i].img_seq = i;
                twistImgs[i].width = 150;
                twistImgs[i].height = 150;
                i++;
            }
            bool[] ret = new bool[cameras.Length];
            i = 0;
            foreach (HaCamera cam in cameras)
            {
                int _ret = NativeMethods.HA_ModifyFacePacket(cam._cam, ref ff, twistImgs, 0, picDatas.Length, ref fi, 1);
                if (_ret != 0)
                {
                    cam.lastErrorCode = _ret;
                    if (!wrongContinue)
                    {
                        Marshal.FreeHGlobal(fi.img);
                        foreach (var timg in twistImgs)
                        {
                            Marshal.FreeHGlobal(timg.img);
                        }
                        return ret;
                    }
                }
                else
                {
                    ret[i] = true;
                }
                i++;
            }
            Marshal.FreeHGlobal(fi.img);
            foreach (var timg in twistImgs)
            {
                Marshal.FreeHGlobal(timg.img);
            }
            return ret;
        }
        /// <summary>
        /// 添加一张人脸模板到设备库中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="pic">图片</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <returns>是否添加成功</returns>
        [Obsolete]
        public bool AddFace(string personID, string personName, int personRole, Bitmap pic, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (pic == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            MemoryStream ms = new MemoryStream();
            pic.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            byte[] bmpBytes = ms.ToArray();
            ms.Close();
            return AddFace(personID, personName, personRole, bmpBytes, wgNo, effectTime);
        }
        /// <summary>
        /// 添加一组图片到同一人员中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="pics">图片</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <returns>是否添加成功</returns>
        [Obsolete]
        public bool AddFace(string personID, string personName, int personRole, Bitmap[] pics, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (pics == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var pic in pics)
            {
                if (pic == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            byte[][] picDatas = new byte[pics.Length][];
            for (int i = 0; i < pics.Length; ++i)
            {
                var pic = pics[i];
                MemoryStream ms = new MemoryStream();
                pic.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                picDatas[i] = ms.ToArray();
                ms.Close();
            }
            return AddFace(personID, personName, personRole, picDatas, wgNo, effectTime);
        }
        /// <summary>
        /// <para>添加一组图片到同一人员中</para>
        /// <para>一组图片中单个失败不影响其他图片下发</para>
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picPaths">图片路径（最多五张图片）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <param name="err_codes">错误码，需要在外部分配与picPaths长度相等的数组</param>
        /// <returns>是否添加成功</returns>
        public bool AddFaceEx(string personID, string personName, int personRole, string[] picPaths, uint wgNo, uint effectTime, int[] err_codes)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picPaths == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (picPaths.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var item in picPaths)
            {
                if (string.IsNullOrEmpty(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
                if (!File.Exists(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            byte[][] picDatas = new byte[picPaths.Length][];
            for (int i = 0; i < picPaths.Length; ++i)
            {
                picDatas[i] = File.ReadAllBytes(picPaths[i]);
            }
            return AddFaceEx(personID, personName, personRole, picDatas, wgNo, effectTime, err_codes);
        }
        /// <summary>
        /// <para>添加一组图片到同一人员中</para>
        /// <para>一组图片中单个失败不影响其他图片下发</para>
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="pics">图片</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <param name="err_codes">错误码，需要在外部分配与pics长度相等的数组</param>
        /// <returns>是否添加成功</returns>
        [Obsolete]
        public bool AddFaceEx(string personID, string personName, int personRole, Bitmap[] pics, uint wgNo, uint effectTime, int[] err_codes)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (pics == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var pic in pics)
            {
                if (pic == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            byte[][] picDatas = new byte[pics.Length][];
            for (int i = 0; i < pics.Length; ++i)
            {
                var pic = pics[i];
                MemoryStream ms = new MemoryStream();
                pic.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                picDatas[i] = ms.ToArray();
                ms.Close();
            }
            return AddFaceEx(personID, personName, personRole, picDatas, wgNo, effectTime, err_codes);
        }
        /// <summary>
        /// <para>添加一组图片到同一人员中</para>
        /// <para>一组图片中单个失败不影响其他图片下发</para>
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picPaths">图片数据（最多五张图片，需要图片完整数据，通过File.ReadAllBytes读出来的）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <param name="err_codes">错误码，需要在外部分配与picDatas长度相等的数组</param>
        /// <returns>是否添加成功</returns>
        public bool AddFaceEx(string personID, string personName, int personRole, byte[][] picDatas, uint wgNo, uint effectTime, int[] err_codes)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picDatas == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (picDatas.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var item in picDatas)
            {
                if (item == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            if (string.IsNullOrEmpty(personID))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (string.IsNullOrEmpty(personName))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (personRole != 0 && personRole != 1 && personRole != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            FaceFlags ff = new FaceFlags();
            ff.wgCardNO = wgNo;
            ff.effectTime = effectTime;
            ff.faceID = Converter.ConvertStringToUTF8(personID, 20);
            ff.faceName = Converter.ConvertStringToUTF8(personName, 16);
            ff.role = personRole;
            FaceImage[] faceImages = new FaceImage[picDatas.Length];
            for (int i = 0; i < picDatas.Length; ++i)
            {
                faceImages[i].img_fmt = 0;
                faceImages[i].img_seq = i;
                byte[] imgData = picDatas[i];
                faceImages[i].img = Marshal.AllocHGlobal(imgData.Length);
                Marshal.Copy(imgData, 0, faceImages[i].img, imgData.Length);
                faceImages[i].img_len = imgData.Length;
            }
            ErrorFaceImage[] _err_codes = new ErrorFaceImage[picDatas.Length];
            int err_count = 0;
            int ret = NativeMethods.HA_JpgFacesEx(_cam, 0, ref ff, faceImages, picDatas.Length, 1, _err_codes, ref err_count);
            if (ret != 0)
                lastErrorCode = ret;
            if (err_count > 0)
            {
                foreach (ErrorFaceImage eff in _err_codes)
                {
                    if (eff.err_code != 0)
                        err_codes[eff.img_seq] = eff.err_code;
                }
            }
            if (ret != 0)
                lastErrorCode = ret;
            for (int i = 0; i < picDatas.Length; ++i)
            {
                Marshal.FreeHGlobal(faceImages[i].img);
            }
            return ret == 0;
        }
        /// <summary>
        /// <para>更新一张人脸模板到相机库中</para>
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="picPath">图片路径</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <returns>是否修改成功</returns>
        public bool ModifyFace(string personID, string personName, int personRole, string picPath, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (string.IsNullOrEmpty(picPath))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (!File.Exists(picPath))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            return ModifyFace(personID, personName, personRole, File.ReadAllBytes(picPath), wgNo, effectTime);
        }
        /// <summary>
        /// 更新一组图片到同一人员中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picPaths">图片路径（最多五张图片式）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <returns>是否添加成功</returns>
        public bool ModifyFace(string personID, string personName, int personRole, string[] picPaths, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picPaths == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (picPaths.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var item in picPaths)
            {
                if (string.IsNullOrEmpty(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
                if (!File.Exists(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            byte[][] picDatas = new byte[picPaths.Length][];
            for (int i = 0; i < picPaths.Length; ++i)
            {
                picDatas[i] = File.ReadAllBytes(picPaths[i]);
            }
            return ModifyFace(personID, personName, personRole, picDatas, wgNo, effectTime);
        }
        /// <summary>
        /// <para>更新一张人脸模板到相机库中</para>
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="picData">图片数据（图片完整数据，通过File.ReadAllBytes读出来的）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <returns>是否修改成功</returns>
        public bool ModifyFace(string personID, string personName, int personRole, byte[] picData, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picData == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (string.IsNullOrEmpty(personID))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (string.IsNullOrEmpty(personName))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (personRole != 0 && personRole != 1 && personRole != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            FaceFlags ff = new FaceFlags();
            ff.wgCardNO = wgNo;
            ff.effectTime = effectTime;
            ff.faceID = Converter.ConvertStringToUTF8(personID, 20);
            ff.faceName = Converter.ConvertStringToUTF8(personName, 16);
            ff.role = personRole;
            IntPtr picDataPtr = Marshal.AllocHGlobal(picData.Length);
            Marshal.Copy(picData, 0, picDataPtr, picData.Length);
            int ret = NativeMethods.HA_ModifyJpgFace(_cam, ref ff, picDataPtr, picData.Length);
            Marshal.FreeHGlobal(picDataPtr);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 更新一组图片到同一人员中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picDatas">图片数据（最多五张图片，需要图片完整数据，通过File.ReadAllBytes读出来的）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <returns>是否添加成功</returns>
        public bool ModifyFace(string personID, string personName, int personRole, byte[][] picDatas, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picDatas == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (picDatas.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var item in picDatas)
            {
                if (item == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            if (string.IsNullOrEmpty(personID))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (string.IsNullOrEmpty(personName))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (personRole != 0 && personRole != 1 && personRole != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            FaceFlags ff = new FaceFlags();
            ff.wgCardNO = wgNo;
            ff.effectTime = effectTime;
            ff.faceID = Converter.ConvertStringToUTF8(personID, 20);
            ff.faceName = Converter.ConvertStringToUTF8(personName, 16);
            ff.role = personRole;
            FaceImage[] faceImages = new FaceImage[picDatas.Length];
            for (int i = 0; i < picDatas.Length; ++i)
            {
                faceImages[i].img_fmt = 0;
                faceImages[i].img_seq = i;
                byte[] imgData = picDatas[i];
                faceImages[i].img = Marshal.AllocHGlobal(imgData.Length);
                Marshal.Copy(imgData, 0, faceImages[i].img, imgData.Length);
                faceImages[i].img_len = imgData.Length;
            }
            int ret = NativeMethods.HA_ModifyJpgFaces(_cam, ref ff, faceImages, picDatas.Length, 1);
            if (ret != 0)
                lastErrorCode = ret;
            for (int i = 0; i < picDatas.Length; ++i)
            {
                Marshal.FreeHGlobal(faceImages[i].img);
            }
            return ret == 0;
        }
        /// <summary>
        /// 修改一张人脸模板到设备库中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="pic">图片</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <returns>是否修改成功</returns>
        [Obsolete]
        public bool ModifyFace(string personID, string personName, int personRole, Bitmap pic, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (pic == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            MemoryStream ms = new MemoryStream();
            pic.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            byte[] bmpBytes = ms.ToArray();
            ms.Close();
            return ModifyFace(personID, personName, personRole, bmpBytes, wgNo, effectTime);
        }
        /// <summary>
        /// 修改一组图片到同一人员中
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="pics">图片</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <returns>是否添加成功</returns>
        [Obsolete]
        public bool ModifyFace(string personID, string personName, int personRole, Bitmap[] pics, uint wgNo, uint effectTime)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (pics == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var pic in pics)
            {
                if (pic == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            byte[][] picDatas = new byte[pics.Length][];
            for (int i = 0; i < pics.Length; ++i)
            {
                MemoryStream ms = new MemoryStream();
                pics[i].Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                picDatas[i] = ms.ToArray();
                ms.Close();
            }
            return ModifyFace(personID, personName, personRole, picDatas, wgNo, effectTime);
        }
        /// <summary>
        /// <para>更新一组图片到同一人员中</para>
        /// <para>一组图片中单个失败不影响其他图片下发</para>
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picPaths">图片路径（最多五张图片式）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <param name="err_codes">错误码，需要在外部分配与picPaths长度相等的数组</param>
        /// <returns>是否添加成功</returns>
        public bool ModifyFaceEx(string personID, string personName, int personRole, string[] picPaths, uint wgNo, uint effectTime, int[] err_codes)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picPaths == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (picPaths.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var item in picPaths)
            {
                if (string.IsNullOrEmpty(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
                if (!File.Exists(item))
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            byte[][] picDatas = new byte[picPaths.Length][];
            for (int i = 0; i < picPaths.Length; ++i)
            {
                picDatas[i] = File.ReadAllBytes(picPaths[i]);
            }
            return ModifyFaceEx(personID, personName, personRole, picDatas, wgNo, effectTime, err_codes);
        }
        /// <summary>
        /// <para>更新一组图片到同一人员中</para>
        /// <para>一组图片中单个失败不影响其他图片下发</para>
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色，0：普通人员。 1：白名单人员。 2：黑名单人员。</param>
        /// <param name="pics">图片</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间 0xFFFFFFFF永不过期 0永久失效 其他值UTC秒数</param>
        /// <param name="err_codes">错误码，需要在外部分配与pics长度相等的数组</param>
        /// <returns>是否添加成功</returns>
        [Obsolete]
        public bool ModifyFaceEx(string personID, string personName, int personRole, Bitmap[] pics, uint wgNo, uint effectTime, int[] err_codes)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (pics == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var pic in pics)
            {
                if (pic == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            byte[][] picDatas = new byte[pics.Length][];
            for (int i = 0; i < pics.Length; ++i)
            {
                MemoryStream ms = new MemoryStream();
                pics[i].Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                picDatas[i] = ms.ToArray();
                ms.Close();
            }
            return ModifyFaceEx(personID, personName, personRole, picDatas, wgNo, effectTime, err_codes);
        }
        /// <summary>
        /// <para>更新一组图片到同一人员中</para>
        /// <para>一组图片中单个失败不影响其他图片下发</para>
        /// </summary>
        /// <param name="personID">人员编号</param>
        /// <param name="personName">人员姓名</param>
        /// <param name="personRole">人员角色</param>
        /// <param name="picDatas">图片数据（最多五张图片，需要图片完整数据，通过File.ReadAllBytes读出来的）</param>
        /// <param name="wgNo">韦根卡号</param>
        /// <param name="effectTime">过期时间</param>
        /// <param name="err_codes">错误码，需要在外部分配与picPaths长度相等的数组</param>
        /// <returns>是否添加成功</returns>
        public bool ModifyFaceEx(string personID, string personName, int personRole, byte[][] picDatas, uint wgNo, uint effectTime, int[] err_codes)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (picDatas == null)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (picDatas.Length > 5)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            foreach (var item in picDatas)
            {
                if (item == null)
                {
                    lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                    return false;
                }
            }
            if (string.IsNullOrEmpty(personID))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (string.IsNullOrEmpty(personName))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            if (personRole != 0 && personRole != 1 && personRole != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            FaceFlags ff = new FaceFlags();
            ff.wgCardNO = wgNo;
            ff.effectTime = effectTime;
            ff.faceID = Converter.ConvertStringToUTF8(personID, 20);
            ff.faceName = Converter.ConvertStringToUTF8(personName, 16);
            ff.role = personRole;
            FaceImage[] faceImages = new FaceImage[picDatas.Length];
            for (int i = 0; i < picDatas.Length; ++i)
            {
                faceImages[i].img_fmt = 0;
                faceImages[i].img_seq = i;
                byte[] imgData = picDatas[i];
                faceImages[i].img = Marshal.AllocHGlobal(imgData.Length);
                Marshal.Copy(imgData, 0, faceImages[i].img, imgData.Length);
                faceImages[i].img_len = imgData.Length;
            }
            ErrorFaceImage[] _err_codes = new ErrorFaceImage[picDatas.Length];
            int err_count = 0;
            int ret = NativeMethods.HA_JpgFacesEx(_cam, 1, ref ff, faceImages, picDatas.Length, 1, _err_codes, ref err_count);
            if (ret != 0)
                lastErrorCode = ret;
            if (err_count > 0)
            {
                foreach (ErrorFaceImage eff in _err_codes)
                {
                    if (eff.err_code != 0)
                        err_codes[eff.img_seq] = eff.err_code;
                }
            }
            if (ret != 0)
                lastErrorCode = ret;
            for (int i = 0; i < picDatas.Length; ++i)
            {
                Marshal.FreeHGlobal(faceImages[i].img);
            }
            return ret == 0;
        }
        /// <summary>
        /// 删除指定人脸模板
        /// </summary>
        /// <param name="personID">模板编号</param>
        /// <returns>是否删除成功</returns>
        public bool DeleteFace(string personID)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (string.IsNullOrEmpty(personID))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            IntPtr personIDPtr = Converter.ConvertStringToUTF8Unmanaged(personID);
            int ret = NativeMethods.HA_DeleteFaceDataByPersonID(_cam, personIDPtr);
            Marshal.FreeHGlobal(personIDPtr);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 删除所有人脸模板
        /// </summary>
        /// <returns>是否操作成功</returns>
        public bool DeleteAllFace()
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int ret = NativeMethods.HA_DeleteFaceDataAll(_cam);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 获取已注册的人脸模板数
        /// </summary>
        /// <param name="personRole">人员角色 0：普通人员。 1：白名单人员。 2：黑名单人员。 -1：所有人员。</param>
        /// <returns>返回已经添加的人脸总数；如果返回0，可能是出错了(也可能确实是没有模板)，通过错误代码排查</returns>
        public int GetFaceCount(int role)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            _faceCount = 0;
            if (role != -1 && role != 0 && role != 1 && role != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return 0;
            }
            int ret = NativeMethods.HA_QueryByRole(_cam, role, 1, 1, false, false);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return 0;
            }
            if (_queryFacesPageSemaphore.WaitOne(300))
            {
                return _faceCount;
            }
            lastErrorCode = NativeConstants.ERR_TIMEOUT;
            return 0;
        }
        /*/// <summary>
        /// 获取已注册的人脸模板数
        /// </summary>
        /// <returns>返回已经添加的人脸总数；如果返回0，可能是出错了(也可能确实是没有模板)，通过错误代码排查</returns>
        public int GetFaceCount()
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int ret = NativeMethods.HA_GetFaceIDTotal(_cam);
            if (ret < 0)
                lastErrorCode = ret;
            return Math.Max(ret, 0);
        }
        /// <summary>
        /// 查询一张人脸模板数据
        /// </summary>
        /// <param name="index">模板在库中的索引</param>
        /// <param name="getFeatureOut">是否取得模板的特征值</param>
        /// <param name="getImageOut">是否取得模板的图像</param>
        /// <returns>返回查询到的人脸模板数据，可能为空；为空时可能需要排查错误</returns>
        public FaceEntity GetFace(int index, bool getFeatureOut, bool getImageOut)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            QueryFaceFlags qff = new QueryFaceFlags();
            int ret = NativeMethods.HA_QueryFaceID(_cam, ref qff, index, getFeatureOut, getImageOut);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return null;
            }
            FaceEntity fe = new FaceEntity();
            fe.PersonID = Encoding.Default.GetString(Converter.ConvertStringToDefault(qff.personID));
            fe.PersonName = Encoding.Default.GetString(Converter.ConvertStringToDefault(qff.personName));
            if (getFeatureOut && qff.featureSize > 0)
            {
                fe.FeatureData = new byte[1][];
                fe.FeatureData[0] = new byte[qff.featureSize];
                Array.Copy(qff.feature, fe.FeatureData, qff.featureSize);
            }
            return fe;
        }*/
        /// <summary>
        /// 设置串口参数
        /// </summary>
        /// <param name="comIndex">串口编号；只能是NativeConstants.HA_SERIA_RS485或NativeConstants.HA_SERIA_RS232</param>
        /// <param name="parameter">串口参数</param>
        /// <returns>是否设置成功</returns>
        public bool SetSerialParameter(int comIndex, SerialParameter parameter)
        {
            lastErrorCode = 0;
            int ret = NativeMethods.HA_OpenTSerial(_cam, comIndex, parameter.Baudrate, parameter.Parity, parameter.Databit, parameter.Stopbit);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 获取串口参数
        /// </summary>
        /// <param name="comIndex">串口编号；只能是NativeConstants.HA_SERIA_RS485或NativeConstants.HA_SERIA_RS232</param>
        /// <returns>串口参数；可能为空表示未成功，需要排查错误</returns>
        public SerialParameter GetSerialParameter(int comIndex)
        {
            lastErrorCode = 0;
            SerialParameter p = new SerialParameter();
            int _baudrate = 0, _partity = 0, _databit = 0, _stopbit = 0;
            int ret = NativeMethods.HA_GetTSerial(_cam, comIndex, ref _baudrate, ref _partity, ref _databit, ref _stopbit);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return null;
            }
            p.Baudrate = _baudrate;
            p.Parity = _partity;
            p.Databit = _databit;
            p.Stopbit = _stopbit;
            return p;
        }
        /// <summary>
        /// 发送串口数据
        /// </summary>
        /// <param name="comIndex">串口编号；只能是NativeConstants.HA_SERIA_RS485或NativeConstants.HA_SERIA_RS232</param>
        /// <param name="dataToSend">要发送的数据</param>
        /// <returns>是否发送成功</returns>
        public bool WriteSerialData(int comIndex, byte[] dataToSend)
        {
            lastErrorCode = 0;
            if (dataToSend == null || dataToSend.Length < 1)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            IntPtr dataPtr = Marshal.AllocHGlobal(dataToSend.Length);
            Marshal.Copy(dataToSend, 0, dataPtr, dataToSend.Length);
            int ret = NativeMethods.HA_WriteTSerial(_cam, comIndex, dataPtr, dataToSend.Length);
            Marshal.FreeHGlobal(dataPtr);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 获取相机确信分数
        /// </summary>
        /// <returns>确信分数，0-100；为0时有可能时发生了错误，需要排查</returns>
        public int GetReferenceScore()
        {
            lastErrorCode = 0;
            int score = 0;
            int ret = NativeMethods.HA_GetMatchScore(_cam, ref score);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return 0;
            }
            return score;
        }
        /// <summary>
        /// 设置相机确信分数
        /// </summary>
        /// <param name="score">确信分数，0-100</param>
        /// <returns>是否设置成功</returns>
        public bool SetReferenceScore(int score)
        {
            lastErrorCode = 0;
            int ret = NativeMethods.HA_SetMatchScore(_cam, score);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 获取输出延时
        /// </summary>
        /// <returns>输出延时（1-30秒）；如果为0，则可能是出错了，需要排查</returns>
        public int GetOutputDelay()
        {
            lastErrorCode = 0;
            int delay = 0;
            int ret = NativeMethods.HA_GetOutputDelay(_cam, ref delay);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return 0;
            }
            return delay;
        }
        /// <summary>
        /// 设置输出延时
        /// </summary>
        /// <param name="delay">输出延时（1-30秒）</param>
        /// <returns>是否设置成功</returns>
        public bool SetOutputDelay(int delay)
        {
            lastErrorCode = 0;
            int ret = NativeMethods.HA_SetOutputDelay(_cam, delay);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 获取图像输出方式
        /// </summary>
        /// <returns>0:未初始化 1:全景 2:特写 3:全景+特写；取得0时可能是出现错误，需要排查</returns>
        public int GetImageOutputType()
        {
            lastErrorCode = 0;
            int type = 0;
            int ret = NativeMethods.HA_GetOutputCtl(_cam, ref type);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return 0;
            }
            return type;
        }
        /// <summary>
        /// 设置图片输出方式
        /// </summary>
        /// <param name="type">0:未初始化 1:全景 2:特写 3:全景+特写</param>
        /// <returns>是否设置成功</returns>
        public bool SetImageOutputType(int type)
        {
            lastErrorCode = 0;
            int ret = NativeMethods.HA_SetOutputCtl(_cam, type);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 获取对比强度
        /// </summary>
        /// <returns>0:未初始化 1:低 2:中 3:高</returns>
        public int GetMatchLevel()
        {
            lastErrorCode = 0;
            int level = 0;
            int ret = NativeMethods.HA_GetMatchLevel(_cam, ref level);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return 0;
            }
            return level;
        }
        /// <summary>
        /// 设置对比强度
        /// </summary>
        /// <param name="level">0:未初始化 1:低 2:中 3:高</param>
        /// <returns>是否设置成功</returns>
        public bool SetMatchLevel(int level)
        {
            lastErrorCode = 0;
            int ret = NativeMethods.HA_SetMatchLevel(_cam, level);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 获取对比开关
        /// </summary>
        /// <returns>是否开启了对比</returns>
        public bool GetMatchEnable()
        {
            lastErrorCode = 0;
            bool enable = false;
            int ret = NativeMethods.HA_GetMatchEnable(_cam, ref enable);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return enable;
        }
        /// <summary>
        /// 设置对比开关
        /// </summary>
        /// <param name="enable">是否开启对比</param>
        /// <returns>是否设置成功</returns>
        public bool SetMatchEnable(bool enable)
        {
            lastErrorCode = 0;
            int ret = NativeMethods.HA_SetMatchEnable(_cam, enable);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 获取调试开关
        /// </summary>
        /// <returns>是否开启了调试</returns>
        public bool GetDebugEnable()
        {
            lastErrorCode = 0;
            bool enable = false;
            int ret = NativeMethods.HA_GetDebugEnable(_cam, ref enable);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return enable;
        }
        /// <summary>
        /// 设置调试开关
        /// </summary>
        /// <param name="enable">是否开启调试</param>
        /// <returns>是否设置成功</returns>
        public bool SetDebugEnable(bool enable)
        {
            lastErrorCode = 0;
            int ret = NativeMethods.HA_SetDebugEnable(_cam, enable);
            if (ret != 0)
                lastErrorCode = ret;
            return ret == 0;
        }
        /// <summary>
        /// 断开和相机的连接
        /// </summary>
        public void DisConnect()
        {
            NativeMethods.HA_ClearAllCallbacks(_cam);

            NativeMethods.HA_DisConnect(_cam);
        }
        /// <summary>
        /// 分页查询人脸模板数据
        /// </summary>
        /// <param name="pageNo">页码，从1开始</param>
        /// <param name="pageSize">页大小，1~100之间的数值</param>
        /// <param name="role">要查询的人员角色 0：普通人员。 1：白名单人员。 2：黑名单人员。 -1：所有人员。</param>
        /// <param name="fetchFeatures">是否要获取人员的特征值</param>
        /// <param name="timeOutInMilli">超时时间</param>
        /// <returns>查询到的模板数据；可能返回null，返回null时可能是出错了，需要排查</returns>
        public FaceEntity[] QueryFaces(int pageNo, int pageSize, int role, bool fetchFeatures, int timeOutInMilli)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            _queriedFaces.Clear();
            if (pageNo < 1)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            if (pageSize < 1 || pageSize > 100)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            if (role != -1 && role != 0 && role != 1 && role != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            int ret = NativeMethods.HA_QueryByRole(_cam, role, pageNo, pageSize, fetchFeatures, false);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return null;
            }
            if (_queryFacesPageSemaphore.WaitOne(timeOutInMilli))
            {
                return _queriedFaces.ToArray();
            }
            lastErrorCode = NativeConstants.ERR_TIMEOUT;
            return null;
        }
        /// <summary>
        /// 分页查询人脸模板数据
        /// </summary>
        /// <param name="pageNo">页码，从1开始</param>
        /// <param name="pageSize">页大小，1~100之间的数值</param>
        /// <param name="role">要查询的人员角色 0：普通人员。 1：白名单人员。 2：黑名单人员。 -1：所有人员。</param>
        /// <param name="fetchFeatures">是否要获取人员的特征值</param>
        /// <param name="totalCount">符合条件的记录总数</param>
        /// <param name="timeOutInMilli">超时时间</param>
        /// <returns>查询到的模板数据；可能返回null，返回null时可能是出错了，需要排查</returns>
        public FaceEntity[] QueryFaces(int pageNo, int pageSize, int role, bool fetchFeatures, ref int totalCount, int timeOutInMilli)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            totalCount = 0;
            _queriedFaces.Clear();
            if (pageNo < 1)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            if (pageSize < 1 || pageSize > 100)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            if (role != -1 && role != 0 && role != 1 && role != 2)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            int ret = NativeMethods.HA_QueryByRole(_cam, role, pageNo, pageSize, fetchFeatures, false);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return null;
            }
            if (_queryFacesPageSemaphore.WaitOne(timeOutInMilli))
            {
                totalCount = _faceCount;
                return _queriedFaces.ToArray();
            }
            lastErrorCode = NativeConstants.ERR_TIMEOUT;
            return null;
        }
        /// <summary>
        /// 获取设备网络信息
        /// </summary>
        /// <param name="ip">设备IP</param>
        /// <param name="mac">设备MAC地址</param>
        /// <param name="netmask">设备子网掩码</param>
        /// <param name="gateway">设备默认网关</param>
        /// <returns>是否获取成功</returns>
        public bool GetNetInfo(out string ip, out string mac, out string netmask, out string gateway)
        {
            ip = null;
            mac = null;
            netmask = null;
            gateway = null;
            lastErrorCode = NativeConstants.ERR_NONE;
            SystemNetInfo sni = new SystemNetInfo();
            int ret = NativeMethods.HA_GetNetConfig(_cam, ref sni);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            ip = Encoding.Default.GetString(sni.ip_addr);
            mac = Encoding.Default.GetString(sni.mac_addr);
            netmask = Encoding.Default.GetString(sni.netmask);
            gateway = Encoding.Default.GetString(sni.gateway);
            return true;
        }
        /// <summary>
        /// 设置设备IP，不改动其掩码和网关
        /// </summary>
        /// <param name="ip">要设置的值</param>
        /// <returns>是否设置成功；如果设置成功，设备会自行重启</returns>
        public bool SetIp(string ip)
        {
            return SetIp(ip, null, null);
        }
        /// <summary>
        /// 设置设备IP和网关，不改动其掩码
        /// </summary>
        /// <param name="ip">要设置的ip</param>
        /// <param name="gateway">要设置的网关</param>
        /// <returns>是否设置成功；如果设置成功，设备会自行重启</returns>
        public bool SetIp(string ip, string gateway)
        {
            return SetIp(ip, null, gateway);
        }
        /// <summary>
        /// 设置设备IP地址、子网掩码和网关
        /// </summary>
        /// <param name="ip">要设置的ip</param>
        /// <param name="netmask">要设置的掩码；如果为空，表示维持原来的值</param>
        /// <param name="gateway">要设置的网关；如果为空，表示维持原来的值</param>
        /// <returns>是否设置成功；如果设置成功，设备会自行重启</returns>
        public bool SetIp(string ip, string netmask, string gateway)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (string.IsNullOrEmpty(ip))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            SystemNetInfo sni = new SystemNetInfo();
            int ret = NativeMethods.HA_GetNetConfig(_cam, ref sni);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            sni.ip_addr = Converter.ConvertStringToUTF8(ip, 16);
            if (!string.IsNullOrEmpty(netmask))
                sni.netmask = Converter.ConvertStringToUTF8(netmask, 16);
            if (!string.IsNullOrEmpty(gateway))
                sni.gateway = Converter.ConvertStringToUTF8(gateway, 16);
            ret = NativeMethods.HA_SetNetConfig(_cam, ref sni);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            NativeMethods.HA_SystemReboot(_cam);
            return true;
        }
        /// <summary>
        /// 获取设备识别区域，相对于设备可见画面区域
        /// </summary>
        /// <returns>设备识别区域；可能为null</returns>
        public Rectangle? GetRecoRect()
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            /*FaceSystemConfig fsc = new FaceSystemConfig();
            int ret = NativeMethods.HA_GetFaceSystemCfg(_cam, ref fsc);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return null;
            }
            Rectangle recoRect = new Rectangle();
            byte[] priv = fsc.app.priv.resv;
            GCHandle pinnedPacket = GCHandle.Alloc(priv, GCHandleType.Pinned);
            FacePrivateParam fpp = (FacePrivateParam) Marshal.PtrToStructure(pinnedPacket.AddrOfPinnedObject(), typeof(FacePrivateParam));
            recoRect.X = fpp.roi[0].x;
            recoRect.Y = fpp.roi[0].y;
            recoRect.Width = fpp.roi[2].x - fpp.roi[0].x;
            recoRect.Height = fpp.roi[2].y - fpp.roi[0].y;
            pinnedPacket.Free();*/
            ha_rect rect = new ha_rect();
            int ret = NativeMethods.HA_GetDetectRect(_cam, ref rect);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return null;
            }
            Rectangle recoRect = new Rectangle();
            recoRect.X = rect.x;
            recoRect.Y = rect.y;
            recoRect.Width = rect.width;
            recoRect.Height = rect.height;
            return recoRect;
        }
        /// <summary>
        /// 白名单开闸
        /// </summary>
        /// <returns>操作是否成功</returns>
        public bool WhiteListAlarm()
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int ret = NativeMethods.HA_WhiteListAlarm(_cam, 1, 1);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 黑名单开闸
        /// </summary>
        /// <returns>操作是否成功</returns>
        public bool BlackListAlarm()
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int ret = NativeMethods.HA_BlackListAlarm(_cam, 1, 1);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// <para>在实时视频的基础上显示识别区域框</para>
        /// <para>可以再次调用此函数表示刷新区域</para>
        /// </summary>
        /// <returns>是否显示成功</returns>
        public bool StartDrawRecoRect()
        {
            Rectangle? recoRect = GetRecoRect();
            if (recoRect == null) return false;
            ha_rect rect = new ha_rect();
            rect.x = (short)recoRect.Value.X;
            rect.y = (short)recoRect.Value.Y;
            rect.width = (short)recoRect.Value.Width;
            rect.height = (short)recoRect.Value.Height;
            NativeMethods.HA_SetDrawRect(_cam, ref rect);
            NativeMethods.HA_StartDrawRect(_cam);
            return true;
        }
        /// <summary>
        /// 隐藏识别区域框的显示
        /// </summary>
        public void StopDrawRecoRect()
        {
            NativeMethods.HA_StopDrawRect(_cam);
        }
        /// <summary>
        /// 获取设备信息
        /// </summary>
        /// <param name="device_id">设备编号；如未设置，会返回string.Empty而非null</param>
        /// <param name="addr_id">点位编号；如未设置，会返回string.Empty而非null</param>
        /// <param name="addr_name">点位名称；如未设置，会返回string.Empty而非null</param>
        /// <returns>是否获取成功</returns>
        public bool GetDeviceInfo(out string device_id, out string addr_id, out string addr_name)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            FaceSystemConfig fsc = new FaceSystemConfig();
            int ret = NativeMethods.HA_GetFaceSystemCfg(_cam, ref fsc);
            if (ret != 0)
            {
                lastErrorCode = ret;
                device_id = string.Empty;
                addr_id = string.Empty;
                addr_name = string.Empty;
                return false;
            }
            device_id = Encoding.UTF8.GetString(fsc.app.common.description.device_id);
            addr_id = Encoding.UTF8.GetString(fsc.app.common.description.addr_id);
            addr_name = Encoding.UTF8.GetString(fsc.app.common.description.addr_name);
            return true;
        }
        /// <summary>
        /// 设置设备信息
        /// </summary>
        /// <param name="device_id">设备编号</param>
        /// <param name="addr_id">点位编号</param>
        /// <param name="addr_name">点位名称</param>
        /// <returns>是否设置成功</returns>
        public bool SetDeviceInfo(string device_id, string addr_id, string addr_name)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            FaceSystemConfig fsc = new FaceSystemConfig();
            int ret = NativeMethods.HA_GetFaceSystemCfg(_cam, ref fsc);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            fsc.app.common.description.device_id = Converter.ConvertStringToUTF8(device_id, 32);
            fsc.app.common.description.addr_id = Converter.ConvertStringToUTF8(addr_id, 32);
            fsc.app.common.description.addr_name = Converter.ConvertStringToUTF8(addr_name, 96);
            ret = NativeMethods.HA_SetFaceSystemCfg(_cam, ref fsc);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 获取设备时间
        /// </summary>
        /// <param name="time">出参，接收设备时间字符串；设备时间格式为yyyy/MM/dd HH:mm:ss</param>
        /// <returns>是否获取成功，如果失败，接收到的字符串为string.Empty</returns>
        public bool GetTime(out string time)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            SystemTime st = new SystemTime();
            int ret = NativeMethods.HA_GetSystemTime(_cam, ref st);
            if (ret != 0)
            {
                lastErrorCode = ret;
                time = string.Empty;
                return false;
            }
            time = Encoding.Default.GetString(Converter.ConvertStringToDefault(st.date)) + " " + Encoding.Default.GetString(Converter.ConvertStringToDefault(st.time));
            return true;
        }
        /// <summary>
        /// 设置设备时间
        /// </summary>
        /// <param name="time">要设置到设备的时间，格式必须为yyyy/MM/dd HH:mm:ss</param>
        /// <returns>是否设置成功</returns>
        public bool SetTime(string time)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (string.IsNullOrEmpty(time))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            DateTime _time;
            if (!DateTime.TryParseExact(time, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AllowInnerWhite, out _time))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            string[] times = time.Split(' ');
            SystemTime st = new SystemTime();
            int ret = NativeMethods.HA_GetSystemTime(_cam, ref st);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            st.date = Converter.ConvertStringToUTF8(times[0], 11);
            st.time = Converter.ConvertStringToUTF8(times[1], 9);
            ret = NativeMethods.HA_SetSystemTime(_cam, ref st);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 设置验证信息
        /// </summary>
        /// <param name="origin_username">原始用户名</param>
        /// <param name="origin_password">原始密码</param>
        /// <param name="new_username">新的用户名（为空表示不修改）</param>
        /// <param name="new_password">新的密码（为空表示不修改）</param>
        /// <param name="auth_enable">是否启用登陆验证，如果关闭，下次登陆传递的用户名密码则不做校验</param>
        /// <returns></returns>
        public bool SetAuthInfo(string origin_username, string origin_password, string new_username, string new_password, bool auth_enable)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (string.IsNullOrEmpty(origin_username) || string.IsNullOrEmpty(origin_password))
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            AuthParam ap = new AuthParam();
            int ret = NativeMethods.HA_GetAuthInfo(_cam, origin_username, origin_password, ref ap);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            if (!string.IsNullOrEmpty(new_username))
                ap.user_name = new_username;
            if (!string.IsNullOrEmpty(new_password))
                ap.passwd = new_password;
            ap.enable = (byte)(auth_enable ? 1 : 0);
            ret = NativeMethods.HA_SetAuthInfo(_cam, origin_username, origin_password, ref ap);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 韦根开闸
        /// </summary>
        /// <returns>操作是否成功</returns>
        public bool WiegandAlarm(uint wgNo)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int ret = NativeMethods.HA_WiegandAlarm(_cam, wgNo);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 获取相机工作模式
        /// </summary>
        /// <returns>相机当前工作模式 1：自动模式 2：在线模式 3：离线模式；如果获取失败，返回0</returns>
        public int GetCamMode()
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            FaceAppParam fap = new FaceAppParam();
            int ret = NativeMethods.HA_GetFaceAppParam(_cam, ref fap);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return 0;
            }
            return fap.cam_mode;
        }
        /// <summary>
        /// 设置相机工作模式
        /// </summary>
        /// <param name="camMode">1：自动模式 2：在线模式 3：离线模式</param>
        /// <returns>是否设置成功</returns>
        public bool SetCamMode(int camMode)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            if (camMode != 1 && camMode != 2 && camMode != 3)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            FaceAppParam fap = new FaceAppParam();
            int ret = NativeMethods.HA_GetFaceAppParam(_cam, ref fap);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            fap.cam_mode = (byte)camMode;
            ret = NativeMethods.HA_SetFaceAppParam(_cam, ref fap);
            if (ret != 0)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return false;
            }
            return true;
        }
        /// <summary>
        /// <para>是否打开视频流</para>
        /// <para>此函数功能直接影响相机对此连接的传输，避免网络传输</para>
        /// </summary>
        /// <param name="open">是否打开视频流</param>
        /// <returns>是否切换成功</returns>
        public bool SwitchStreamTrans(bool open)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int ret = NativeMethods.HA_LiveStreamCtl(_cam, open ? 1 : 0);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// <para>获取或设置脱机记录存储启用标识</para>
        /// <para>设备是否存储未成功上传的抓拍记录到TF卡</para>
        /// </summary>
        public bool RecorderEnable
        {
            get
            {
                byte _val = 0;
                NativeMethods.HA_GetRecorderEnable(_cam, ref _val);
                return _val == 1;
            }
            set
            {
                byte _val = (byte)(value ? 1 : 0);
                NativeMethods.HA_SetRecorderEnable(_cam, _val);
            }
        }
        /// <summary>
        /// <para>获取或设置脱机记录断点续传启用标识</para>
        /// <para>设备是否重新发送未成功上传的抓拍记录到服务器</para>
        /// </summary>
        public bool RecorderResumeEnable
        {
            get
            {
                byte _val = 0;
                NativeMethods.HA_GetRecorderResumeEnable(_cam, ref _val);
                return _val == 1;
            }
            set
            {
                byte _val = (byte)(value ? 1 : 0);
                NativeMethods.HA_SetRecorderResumeEnable(_cam, _val);
            }
        }
        /// <summary>
        /// 删除存储在设备TF卡中的历史记录
        /// </summary>
        /// <param name="sequnceno">要删除的历史记录编号，null表示删除所有</param>
        /// <returns>是否删除成功</returns>
        public bool DeleteFaceRecord(uint? sequnceno)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int ret = 0;
            if (sequnceno == null)
                ret = NativeMethods.HA_DeleteFaceRecordAll(_cam);
            else
                ret = NativeMethods.HA_DeleteFaceRecordBySequence(_cam, sequnceno.Value);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 分页查询脱机存储记录
        /// </summary>
        /// <param name="pageNo">页码，从1开始</param>
        /// <param name="pageSize">页大小，需小于20</param>
        /// <param name="condition">查询条件，组合内容</param>
        /// <param name="timeOutInMilli">超时时间</param>
        /// <returns>查询到的脱机记录数据；可能返回null，返回null时可能是出错了，需要排查</returns>
        public RecordDataEntity[] QueryRecords(int pageNo, int pageSize, RecordQueryCondition condition, int timeOutInMilli)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            _queriedRecords.Clear();
            if (pageNo < 1)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            if (pageSize < 1 || pageSize > 100)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            RecordCondition rc = ConvertRecordConditionToNative(condition);
            int ret = NativeMethods.HA_QueryFaceRecord(_cam, pageNo, pageSize, ref rc);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return null;
            }
            if (_queryRecordPageSemaphore.WaitOne(timeOutInMilli))
            {
                return _queriedRecords.ToArray();
            }
            lastErrorCode = NativeConstants.ERR_TIMEOUT;
            return null;
        }
        /// <summary>
        /// 分页查询脱机存储记录
        /// </summary>
        /// <param name="pageNo">页码，从1开始</param>
        /// <param name="pageSize">页大小，需小于20</param>
        /// <param name="condition">查询条件，组合内容</param>
        /// <param name="totalCount">符合条件的记录总数</param>
        /// <param name="timeOutInMilli">超时时间</param>
        /// <returns>查询到的脱机记录数据；可能返回null，返回null时可能是出错了，需要排查</returns>
        public RecordDataEntity[] QueryRecords(int pageNo, int pageSize, RecordQueryCondition condition, ref int totalCount, int timeOutInMilli)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            _queriedRecords.Clear();
            totalCount = 0;
            if (pageNo < 1)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            if (pageSize < 1 || pageSize > 100)
            {
                lastErrorCode = NativeConstants.ERR_INVALID_PARAM;
                return null;
            }
            RecordCondition rc = ConvertRecordConditionToNative(condition);
            int ret = NativeMethods.HA_QueryFaceRecord(_cam, pageNo, pageSize, ref rc);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return null;
            }
            if (_queryRecordPageSemaphore.WaitOne(timeOutInMilli))
            {
                totalCount = _recordCount;
                return _queriedRecords.ToArray();
            }
            lastErrorCode = NativeConstants.ERR_TIMEOUT;
            return null;
        }
        /// <summary>
        /// 获取所有人员编号
        /// </summary>
        /// <returns>所有人员编号；返回null表示查询失败；返回空数组表示没有注册人员</returns>
        public string[] GetAllPersonId()
        {
            byte[] idBuf = new byte[20000 * 20];
            lastErrorCode = NativeConstants.ERR_NONE;
            int _count = 0;
            int _total = 0;
            int _ret = NativeMethods.HA_GetAllPersonId(_cam, idBuf, idBuf.Length, ref _count, ref _total);
            if (_ret != 0)
            {
                lastErrorCode = _ret;
                return null;
            }
            if (_count == 0 || _total == 0)
            {
                return new string[0];
            }
            string[] ret = new string[_count];
            for (int i = 0; i < _count; ++i)
            {
                ret[i] = Encoding.UTF8.GetString(idBuf, i * 20, 20);
            }
            return ret;
        }
        /// <summary>
        /// 从设备截图
        /// </summary>
        /// <param name="timeOutInMilli">超时时间</param>
        /// <returns>设备实时画面截图；返回null表示截图失败</returns>
        public Image Snapshot(int timeOutInMilli)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            _snapshotImage = null;
            int ret = NativeMethods.HA_Snapshot(_cam);
            if (ret != 0)
            {
                lastErrorCode = ret;
                return null;
            }
            if (_snapshotSemaphore.WaitOne(timeOutInMilli))
            {
                return _snapshotImage;
            }
            lastErrorCode = NativeConstants.ERR_TIMEOUT;
            return null;
        }
        /// <summary>
        /// 重启设备
        /// </summary>
        /// <returns></returns>
        public bool Reboot()
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int _ret = NativeMethods.HA_SystemReboot(_cam);
            if (_ret != 0)
            {
                lastErrorCode = _ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 录像到文件
        /// </summary>
        /// <param name="savePath">录像文件存储路径</param>
        /// <returns>是否成功启动录像</returns>
        public bool StartRecoVideo(string savePath)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            IntPtr fnPtr = Marshal.StringToHGlobalAnsi(savePath);
            int _ret = NativeMethods.HA_SaveRealDate(_cam, fnPtr);
            Marshal.FreeHGlobal(fnPtr);
            if (_ret != 0)
            {
                lastErrorCode = _ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 设置设备二级密码
        /// </summary>
        /// <param name="passsword">要设置的密码</param>
        /// <returns>是否设置成功</returns>
        public bool SetSDKPassword(string passsword)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int _ret = NativeMethods.HA_SetCamSDKPassword(_cam, passsword);
            if (_ret != 0)
            {
                lastErrorCode = _ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 获取对比模式
        /// </summary>
        /// <returns>相机对比模式</returns>
        public MatchMode GetMatchMode()
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            byte mm = 0;
            int _ret = NativeMethods.HA_GetMatchMode(_cam, ref mm);
            if (_ret != 0)
            {
                lastErrorCode = _ret;
                return MatchMode.MATCH_MODE_NULL;
            }
            return (MatchMode)mm;
        }
        /// <summary>
        /// 设置对比模式
        /// </summary>
        /// <param name="mm">对比模式</param>
        /// <returns>是否设置成功</returns>
        public bool SetMatchMode(MatchMode mm)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int _ret = NativeMethods.HA_SetMatchMode(_cam, (byte)mm);
            if (_ret != 0)
            {
                lastErrorCode = _ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 停止录像
        /// </summary>
        public void StopRecoVideo()
        {
            NativeMethods.HA_StopSaveRealDate(_cam);
        }
        /// <summary>
        /// 读取用户校验码
        /// </summary>
        /// <returns>设备用户校验码</returns>
        public string ReadCustomerAuthCode()
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int readSize = 0;
            byte[] buffer = new byte[256];
            int _ret = NativeMethods.HA_ReadCustomerAuthCode(_cam, buffer, ref readSize);
            if (_ret != 0)
            {
                lastErrorCode = _ret;
                return null;
            }
            return Encoding.UTF8.GetString(buffer, 0, readSize);
        }
        /// <summary>
        /// 写入用户校验码
        /// </summary>
        /// <param name="authCode">设备用户校验码</param>
        /// <returns>是否写入成功</returns>
        public bool WriteCustomerAuthCode(string authCode)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            byte[] buffer = Encoding.UTF8.GetBytes(authCode);
            int _ret = NativeMethods.HA_WriteCustomerAuthCode(_cam, buffer, buffer.Length);
            if (_ret != 0)
            {
                lastErrorCode = _ret;
                return false;
            }
            return true;
        }
        /// <summary>
        /// 播放音频。注：只支持wav格式声音数据(压缩参数：A-Law, 8000Hz,64kbps,mono)
        /// </summary>
        /// <param name="audioFileBytes">语音文件内容</param>
        /// <returns>是否播放成功</returns>
        public bool PlayAudio(byte[] audioFileBytes)
        {
            lastErrorCode = NativeConstants.ERR_NONE;
            int _ret = NativeMethods.HA_PlayAudio(_cam, audioFileBytes, audioFileBytes.Length);
            if (_ret != 0)
            {
                lastErrorCode = _ret;
                return false;
            }
            return true;
        }
        #endregion methods

        private void HookCallbackEx(IntPtr cam)
        {
            _connectEventCallback = ConnectEventCb;
            NativeMethods.HA_RegConnectEventCbEx(cam, _connectEventCallback, 0);

            _faceRecoCallback = FaceCaptureEventCb;
            NativeMethods.HA_RegFaceRecoCb(cam, _faceRecoCallback, IntPtr.Zero);

            _serialDataCallback = SerialDataReadCb;
            NativeMethods.HA_RegReadTSerialCbEx(_cam, _serialDataCallback, 0);

            _faceQueryCallback = FaceQueryCb;
            NativeMethods.HA_RegFaceQueryCb(_cam, _faceQueryCallback, IntPtr.Zero);

            _alarmRecordCallback = AlarmRecordCb;
            NativeMethods.HA_RegAlarmRecordCb(_cam, _alarmRecordCallback, IntPtr.Zero);

            _alarmRequestCallback = AlarmRequestCb;
            NativeMethods.HA_RegAlarmRequestCb(_cam, _alarmRequestCallback, IntPtr.Zero);

            _faceRecordQueryCallback = RecordQueryCb;
            NativeMethods.HA_RegFaceRecordQueryCb(_cam, _faceRecordQueryCallback, IntPtr.Zero);

            _snapshotCallback = SnapshotCb;
            NativeMethods.HA_RegSnapshotCb(_cam, _snapshotCallback, IntPtr.Zero);
        }

        #region callback methods
        private void SnapshotCb(IntPtr cam, ref SnapshotImage snapImage, IntPtr usrParam)
        {
            if (snapImage.snapImageSize > 0)
            {
                byte[] b = new byte[snapImage.snapImageSize];
                //Array.Copy(snapImage.snapImage, b, snapImage.snapImageSize);
                Marshal.Copy(snapImage.snapImage, b, 0, b.Length);
                _snapshotImage = Image.FromStream(new MemoryStream(b));
            }
            _snapshotSemaphore.Release();
        }
        private void RecordQueryCb(IntPtr cam, IntPtr data, IntPtr usrParam)
        {
            RecordData rd = (RecordData)Marshal.PtrToStructure(data, typeof(RecordData));
            if (rd.record_no == 0)
            {
                try
                {
                    _queryRecordPageSemaphore.Release();
                }
                catch { }
                return;
            }
            _recordCount = rd.record_count;
            RecordDataEntity rde = new RecordDataEntity();
            rde.SequenceID = rd.sequence;
            rde.CaptureTime = Converter.ConvertToDateTime(rd.tvSec, rd.tvUsec);
            rde.IsPersonMatched = rd.matched > 0;
            rde.Sex = rd.sex;
            rde.Age = rd.age;
            rde.PersonRole = rd.role;
            if (rde.IsPersonMatched)
            {
                rde.PersonID = Encoding.Default.GetString(Converter.ConvertStringToDefault(rd.person_id));
                rde.PersonName = Encoding.Default.GetString(Converter.ConvertStringToDefault(rd.person_name));
                rde.MatchScore = rd.matched;
            }
            if (rd.face_image_len > 0)
            {
                rde.FeatureImageData = new byte[rd.face_image_len];
                Marshal.Copy(rd.face_image, rde.FeatureImageData, 0, rd.face_image_len);
                rde.FaceInFeature = new System.Drawing.Rectangle(rd.faceXInFaceImg, rd.faceYInFaceImg, rd.faceWInFaceImg, rd.faceHInFaceImg);
            }
            if (rd.reg_image_len > 0)
            {
                rde.ModelFaceImageData = new byte[rd.reg_image_len];
                Marshal.Copy(rd.reg_image, rde.ModelFaceImageData, 0, rd.reg_image_len);
            }
            _queriedRecords.Add(rde);
        }

        private void FaceQueryCb(IntPtr cam, IntPtr data, IntPtr usrParam)
        {
            QueryFaceInfo qfi = (QueryFaceInfo)Marshal.PtrToStructure(data, typeof(QueryFaceInfo));
            if (qfi.record_no == 0)
            {
                try
                {
                    _queryFacesPageSemaphore.Release();
                }
                catch { }
                return;
            }
            _faceCount = qfi.record_count;
            FaceEntity fe = new FaceEntity();
            fe.PersonID = Encoding.Default.GetString(Converter.ConvertStringToDefault(qfi.personID));
            fe.PersonName = Encoding.Default.GetString(Converter.ConvertStringToDefault(qfi.personName));
            fe.PersonRole = qfi.role;
            fe.WiegandNo = qfi.wgCardNO;
            fe.EffectTime = qfi.effectTime;
            if (qfi.feature_count > 0)
            {
                fe.FeatureData = new float[qfi.feature_count][];
                for (int i = 0; i < qfi.feature_count; ++i)
                {
                    fe.FeatureData[i] = new float[qfi.feature_size];
                    unsafe
                    {
                        fixed (float* dst = fe.FeatureData[i])
                        {
                            float* feature = (float*)qfi.feature.ToPointer();
                            for (int j = 0; j < qfi.feature_size; ++j)
                            {
                                dst[j] = feature[j + (i * qfi.feature_size)];
                            }
                        }
                    }
                }
            }
            if (qfi.imgNum > 0)
            {
                fe.ImageData = new byte[qfi.imgNum][];
                for (int i = 0; i < qfi.imgNum; ++i)
                {
                    fe.ImageData[i] = new byte[qfi.imgSize[i]];
                    IntPtr imgData = qfi.imgBuff1;
                    if (i == 1)
                        imgData = qfi.imgBuff2;
                    else if (i == 2)
                        imgData = qfi.imgBuff3;
                    else if (i == 3)
                        imgData = qfi.imgBuff4;
                    else if (i == 4)
                        imgData = qfi.imgBuff5;
                    Marshal.Copy(imgData, fe.ImageData[i], 0, qfi.imgSize[i]);
                }
            }
            _queriedFaces.Add(fe);
        }
        private void ConnectEventCb(IntPtr cam, string ip, ushort port, int evt, int usrParam)
        {
            var e = new ConnectEventArgs { Connected = evt == 1 };
            OnConnectStateChanged(e);
        }
        private void FaceCaptureEventCb(IntPtr cam, IntPtr captureData, IntPtr usrParam)
        {
            FaceRecoInfo fri = (FaceRecoInfo)Marshal.PtrToStructure(captureData, typeof(FaceRecoInfo));
            OnFaceCaptured(ConvertStructureToEventArgs(fri));
        }
        private RecordCondition ConvertRecordConditionToNative(RecordQueryCondition rqc)
        {
            RecordCondition rc = new RecordCondition();
            rc.condition_flag = RecordQueryFlag.NONE;
            if (rqc.ByAge)
            {
                rc.condition_flag |= RecordQueryFlag.RECORD_QUERY_FLAG_AGE;
                rc.age_start = (byte)rqc.AgeStart;
                rc.age_end = (byte)rqc.AgeEnd;
            }
            if (rqc.ByCaptureTime)
            {
                rc.condition_flag |= RecordQueryFlag.RECORD_QUERY_FLAG_TIME;
                rc.time_start = Convert.ToUInt32(rqc.TimeStart.ToUniversalTime().Subtract(DateTime.Parse("1970-1-1")).TotalSeconds);
                rc.time_end = Convert.ToUInt32(rqc.TimeEnd.ToUniversalTime().Subtract(DateTime.Parse("1970-1-1")).TotalSeconds);
            }
            rc.query_mode = rqc.FuzzyMode ? QueryMode.QUERY_FUZZY : QueryMode.QUERY_EXACT;
            if (rqc.ById)
            {
                rc.condition_flag |= RecordQueryFlag.RECORD_QUERY_FLAG_ID;
                byte[] idStrBytes = Encoding.UTF8.GetBytes(rqc.PersonId);
                rc.person_id = new byte[20];
                Array.Copy(idStrBytes, rc.person_id, Math.Min(idStrBytes.Length, 20));
            }
            if (rqc.ByName)
            {
                rc.condition_flag |= RecordQueryFlag.RECORD_QUERY_FLAG_NAME;
                byte[] nameStrBytes = Encoding.UTF8.GetBytes(rqc.PersonName);
                rc.person_name = new byte[16];
                Array.Copy(nameStrBytes, rc.person_name, Math.Min(nameStrBytes.Length, 16));
            }
            if (rqc.ByQValue)
            {
                rc.condition_flag |= RecordQueryFlag.RECORD_QUERY_FLAG_QVALUE;
                rc.qvalue_start = (byte)rqc.QValueStart;
                rc.qvalue_end = (byte)rqc.QValueEnd;
            }
            if (rqc.BySex)
            {
                rc.condition_flag |= RecordQueryFlag.RECORD_QUERY_FLAG_SEX;
                rc.sex = (byte)rqc.Sex;
            }
            if (rqc.ByUploadState)
            {
                rc.condition_flag |= RecordQueryFlag.RECORD_QUERY_FLAG_UPLOAD;
                rc.upload_state = (sbyte)rqc.UploadState;
                if (rqc.UploadState == 2)
                    rc.upload_state = 0;
            }
            if (rqc.ByMatchScore)
            {
                rc.condition_flag |= RecordQueryFlag.RECORD_QUERY_FLAG_SCORE;
                rc.score_start = (short)rqc.MatchScoreStart;
                rc.score_end = (short)rqc.MatchScoreEnd;
            }
            return rc;
        }
        private FaceCapturedEventArgs ConvertStructureToEventArgs(FaceRecoInfo info)
        {
            FaceCapturedEventArgs e = new FaceCapturedEventArgs();
            e.SequenceID = info.sequence;
            e.CameraID = Encoding.Default.GetString(Converter.ConvertStringToDefault(info.camId));
            e.AddrID = Encoding.Default.GetString(Converter.ConvertStringToDefault(info.posId));
            e.AddrName = Encoding.Default.GetString(Converter.ConvertStringToDefault(info.posName));
            e.CaptureTime = Converter.ConvertToDateTime(info.tvSec, info.tvUsec);
            e.IsRealTimeData = info.isRealtimeData != 0;
            e.IsPersonMatched = info.matched > 0;
            e.Sex = info.sex;
            e.Age = info.age;
            e.QValue = info.qValue;
            e.PersonRole = info.matchRole;
            if (e.IsPersonMatched)
            {
                e.PersonID = Encoding.Default.GetString(Converter.ConvertStringToDefault(info.matchPersonId));
                e.PersonName = Encoding.Default.GetString(Converter.ConvertStringToDefault(info.matchPersonName));
                e.MatchScore = info.matched;
            }
            if (info.existImg != 0 && info.imgLen > 0)
            {
                e.EnvironmentImageData = new byte[info.imgLen];
                Marshal.Copy(info.img, e.EnvironmentImageData, 0, info.imgLen);
                e.FaceInEnvironment = new System.Drawing.Rectangle(info.faceXInImg, info.faceYInImg, info.faceWInImg, info.faceHInImg);
            }
            if (info.existFaceImg != 0 && info.faceImgLen > 0)
            {
                e.FeatureImageData = new byte[info.faceImgLen];
                Marshal.Copy(info.faceImg, e.FeatureImageData, 0, info.faceImgLen);
                e.FaceInFeature = new System.Drawing.Rectangle(info.faceXInFaceImg, info.faceYInFaceImg, info.faceWInFaceImg, info.faceHInFaceImg);
            }
            if (info.existVideo != 0 && info.videoLen > 0)
            {
                e.VideoData = new byte[info.videoLen];
                Marshal.Copy(info.video, e.VideoData, 0, info.videoLen);
                e.VideoStartTime = Converter.ConvertToDateTime(info.videoStartSec, info.videoStartUsec);
                e.VideoEndTime = Converter.ConvertToDateTime(info.videoEndSec, info.videoEndUsec);
            }
            if (info.feature_size > 0)
            {
                e.FeatureData = new float[info.feature_size];
                Marshal.Copy(info.feature, e.FeatureData, 0, info.feature_size);
            }
            if (info.modelFaceImgLen > 0)
            {
                e.ModelFaceImageData = new byte[info.modelFaceImgLen];
                Marshal.Copy(info.modelFaceImg, e.ModelFaceImageData, 0, info.modelFaceImgLen);
            }
            return e;
        }
        private static void DiscoverIpCb(IntPtr ipscan, int usrParam)
        {
            ipscan_t t = (ipscan_t)Marshal.PtrToStructure(ipscan, typeof(ipscan_t));
            OnDeviceDiscovered(ConvertStructureToEventArgs(t));
        }
        private static DeviceDiscoverdEventArgs ConvertStructureToEventArgs(ipscan_t ipscan)
        {
            DeviceDiscoverdEventArgs e = new DeviceDiscoverdEventArgs();
            e.IP = Encoding.Default.GetString(Converter.ConvertStringToDefault(ipscan.ip));
            e.Mac = Encoding.Default.GetString(Converter.ConvertStringToDefault(ipscan.mac));
            e.Manufacturer = Encoding.Default.GetString(Converter.ConvertStringToDefault(ipscan.manufacturer));
            e.NetMask = Encoding.Default.GetString(Converter.ConvertStringToDefault(ipscan.netmask));
            e.Plateform = Encoding.Default.GetString(Converter.ConvertStringToDefault(ipscan.platform));
            e.System = Encoding.Default.GetString(Converter.ConvertStringToDefault(ipscan.system));
            e.Version = Encoding.Default.GetString(Converter.ConvertStringToDefault(ipscan.version));
            return e;
        }
        private void SerialDataReadCb(IntPtr cam, int comIndex, IntPtr data, int size, int usrParam)
        {
            SerialDataArrivedEventArgs e = new SerialDataArrivedEventArgs();
            e.Data = new byte[size];
            Marshal.Copy(data, e.Data, 0, size);
            OnSerialDataArrived(e);
        }
        private void AlarmRecordCb(IntPtr cam, ref AlarmInfoRecord alarmRecord, IntPtr usrParam)
        {
            AlarmRecordEventArgs e = new AlarmRecordEventArgs();
            e.AlarmDeviceId = alarmRecord.alarmDeviceId;
            e.AlarmDeviceType = alarmRecord.alarmDeviceType;
            e.AlarmTime = alarmRecord.alarmTime;
            e.CameraID = alarmRecord.cameraID;
            e.PersonID = alarmRecord.personID;
            OnAlarmRecordReceived(e);
        }
        private void AlarmRequestCb(IntPtr cam, ref AlarmRequest alarmRequest, IntPtr usrParam)
        {
            AlarmRequestEventArgs e = new AlarmRequestEventArgs();
            e.CameraID = alarmRequest.cameraID;
            e.AlarmDeviceId = alarmRequest.alarmDeviceId;
            e.AlarmDeviceState = alarmRequest.alarmDeviceState;
            e.AlarmDeviceType = alarmRequest.alarmDeviceType;
            e.PersonID = alarmRequest.personID;
            e.RequestTime = alarmRequest.requestTime;
            OnAlarmRequestReceived(e);
        }
        #endregion callback methods

        #region event triggers
        protected virtual void OnConnectStateChanged(ConnectEventArgs e)
        {
            if (ConnectStateChanged != null)
                ConnectStateChanged.Invoke(this, e);
        }
        protected virtual void OnFaceCaptured(FaceCapturedEventArgs e)
        {
            if (FaceCaptured != null)
                FaceCaptured.Invoke(this, e);
        }
        private static void OnDeviceDiscovered(DeviceDiscoverdEventArgs e)
        {
            if (DeviceDiscovered != null)
                DeviceDiscovered.Invoke(null, e);
        }
        protected virtual void OnSerialDataArrived(SerialDataArrivedEventArgs e)
        {
            if (SerialDataArrived != null)
                SerialDataArrived.Invoke(this, e);
        }
        protected virtual void OnAlarmRequestReceived(AlarmRequestEventArgs e)
        {
            if (AlarmRequestReceived != null)
            {
                AlarmRequestReceived.Invoke(this, e);
                if (!e.Cancel)
                {
                    if (e.AlarmDeviceType == 0)
                    {
                        if (e.AlarmDeviceId == 1)
                        {
                            WhiteListAlarm();
                        }
                        else if (e.AlarmDeviceId == 2)
                        {
                            BlackListAlarm();
                        }
                    }
                    else if (e.AlarmDeviceType == 1)
                    {
                        WiegandAlarm(e.AlarmDeviceId);
                    }
                }
            }
        }
        protected virtual void OnAlarmRecordReceived(AlarmRecordEventArgs e)
        {
            if (AlarmRecordReceived != null)
            {
                AlarmRecordReceived.Invoke(this, e);
            }
        }
        #endregion event triggers
    }
}
