import { getSignInList, signInDel } from "@/apis/signin";
import { injectGlobalData } from "@/provide";
import { computed, reactive } from "vue";

const queue = [];

export const provideDevices = () => {
    //https://api.ipbase.com/v1/json/8.8.8.8
    const globalData = injectGlobalData();
    const machineId = computed(() => globalData.value.config.Client.Id);
    const devices = reactive({
        timer: 0,
        page: {
            Request: {
                Page: 1, Size: +(localStorage.getItem('ps') || '10'), Name: '', Ids: []
            },
            Count: 0,
            List: []
        },

        showDeviceEdit: false,
        deviceInfo: null
    });
    const _getSignList = () => {
        getSignInList(devices.page.Request).then((res) => {
            console.log(res);
            devices.page.Request = res.Request;
            devices.page.Count = res.Count;
            for (let j in res.List) {
                res.List[j].showDel = machineId.value != res.List[j].MachineId && res.List[j].Connected == false;
                res.List[j].showReboot = res.List[j].Connected;
                res.List[j].isSelf = machineId.value == res.List[j].MachineId;


            }
            devices.page.List = res.List.sort((a, b) => b.Connected - a.Connected);
            for (let i = 0; i < devices.page.List.length; i++) {
                queue.push(devices.page.List[i]);
            }
        }).catch((err) => { });
    }
    const _getSignList1 = () => {
        if (globalData.value.api.connected) {
            getSignInList(devices.page.Request).then((res) => {
                for (let j in res.List) {
                    const item = devices.page.List.filter(c => c.MachineId == res.List[j].MachineId)[0];
                    if (item) {
                        item.Connected = res.List[j].Connected;
                        item.Version = res.List[j].Version;
                        item.LastSignIn = res.List[j].LastSignIn;
                        item.Args = res.List[j].Args;
                        item.showDel = machineId.value != res.List[j].MachineId && res.List[j].Connected == false;
                        item.showReboot = res.List[j].Connected;
                        item.isSelf = machineId.value == res.List[j].MachineId;
                    }
                }
                devices.timer = setTimeout(_getSignList1, 5000);
            }).catch((err) => {
                devices.timer = setTimeout(_getSignList1, 5000);
            });
        } else {
            devices.timer = setTimeout(_getSignList1, 5000);
        }
    }

    const getCountryFlag = () => {
        try {
            if (queue.length == 0) {
                setTimeout(getCountryFlag, 1000);
                return;
            }
            const device = queue.shift();
            fetch(`http://ip-api.com/json/${device.IP.split(':')[0]}`).then(async (response) => {
                try {
                    const json = await response.json();
                    device.countryFlag = `https://unpkg.com/flag-icons@7.2.3/flags/4x3/${json.countryCode.toLowerCase()}.svg`;
                } catch (e) { }
                setTimeout(getCountryFlag, 1000);
            }).catch(() => {
                setTimeout(getCountryFlag, 1000);
            });
        } catch (e) {
            setTimeout(getCountryFlag, 1000);
        }
    }
    getCountryFlag();

    const handleDeviceEdit = (row) => {
        devices.deviceInfo = row;
        devices.showDeviceEdit = true;
    }
    const handlePageChange = (page) => {
        if (page) {
            devices.page.Request.Page = page;
        }
        _getSignList();
    }
    const handlePageSizeChange = (size) => {
        if (size) {
            devices.page.Request.Size = size;
            localStorage.setItem('ps', size);
        }
        _getSignList();
    }
    const handleDel = (name) => {
        signInDel(name).then(() => {
            _getSignList();
        });
    }
    const clearDevicesTimeout = () => {
        clearTimeout(devices.timer);
        devices.timer = 0;
    }

    return {
        devices, machineId, _getSignList, _getSignList1, handleDeviceEdit, handlePageChange, handlePageSizeChange, handleDel, clearDevicesTimeout
    }
}